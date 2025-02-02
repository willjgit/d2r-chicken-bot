﻿using D.Models;
using Reloaded.Memory.Sigscan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace D.Core {
    public partial class MemoryReader {
        private static readonly string ProcessName = Encoding.UTF8.GetString(new byte[] { 68, 50, 82 });
        public static int UnitHashTable = 0x20AF660;
        public static int UiSettings = 0x20BF310;
        public static int ExpansionCheck = 0x20BF335;

        private ProcessInfo m_ProcessInfo;
        private MemoryState _mMemoryState;

        private Models.Path mPath = default;
        private Room mRoom = default;
        private RoomEx mRoomEx = default;
        private Level mLevel = default;
        private StatList mStatList = default;
        private UiGameMenus mGameMenus = default;
        private static ILogger<MemoryReader> Logger;
        private GameState? previousState = null;

        public MemoryReader(ILogger<MemoryReader> logger) {
            Logger = logger;
            _mMemoryState = new MemoryState();
            m_ProcessInfo = GetProcessInfo();
        }

        class MemoryState {
            public MemoryState() {
                GameSocket = null;
            }

            public IntPtr UiSettingPtr { get; set; }
            public IntPtr PlayerUnitPtr { get; set; }
            public IntPtr UnitHashtableAddressPtr { get; set; }
            public IntPtr UiSettingAddressPtr { get; set; }
            public InteropTcpHelper.MIB_TCPROW_OWNER_PID? GameSocket { get; set; } = null;

            public UnitAny PlayerUnit = default;
        }

        void Reset() {
            previousState = null;
            _mMemoryState = new MemoryState();
        }
        // search process
        // search player
        // check if we still have the right player

        public class ProcessInfo {
            public Process? Process;
            public IntPtr Handle;
            public IntPtr BaseAddress;
            public IntPtr ProcessWindowHandle;
            public Scanner Scanner;

            public static ProcessInfo Create() {
                try {
                    var process = Process.GetProcessesByName(ProcessName).FirstOrDefault();
                    if (process == null)
                        return null;


                    return new ProcessInfo() {
                        Process = process,
                        BaseAddress = process.MainModule.BaseAddress,
                        Handle = WindowsHelper.OpenProcess((uint)WindowsHelper.ProcessAccessFlags.VirtualMemoryRead, false, process.Id),
                        ProcessWindowHandle = process.MainWindowHandle,
                        Scanner = new Scanner(process, process.MainModule)
                    };
                } catch (Exception e) {
                    Logger.LogError("Get process info {@e}", e);
                    throw;
                }
            }
        }

        public ProcessInfo GetProcessInfo() {
            if (m_ProcessInfo == null || m_ProcessInfo!.Process!.Id != Process.GetProcessesByName(ProcessName).FirstOrDefault()?.Id
                || m_ProcessInfo.ProcessWindowHandle == IntPtr.Zero) {
                Reset();
                return ProcessInfo.Create();
            }

            return m_ProcessInfo;
        }

        // Took from https://github.com/OneXDeveloper/MapAssist/pull/28
        public IntPtr GetUnitHashtableAddress() {
            if (_mMemoryState.UnitHashtableAddressPtr != IntPtr.Zero)
                return _mMemoryState.UnitHashtableAddressPtr;

            var offsetResult = m_ProcessInfo.Scanner.CompiledFindPattern("48 8d ?? ?? ?? ?? ?? 8b d1");
            var resultRelativeAddress = IntPtr.Add(IntPtr.Add(m_ProcessInfo.BaseAddress, offsetResult.Offset), 3);
            var offSetAddress = WindowsHelper.Read<int>(m_ProcessInfo.Handle, resultRelativeAddress);

            return IntPtr.Add(m_ProcessInfo.BaseAddress, offsetResult.Offset + 7 + offSetAddress);
        }

        //https://github.com/OneXDeveloper/MapAssist/blob/main/Helpers/ProcessContext.cs
        public IntPtr GetUiSettingAddress() {
            if (_mMemoryState.UiSettingAddressPtr != IntPtr.Zero)
                return _mMemoryState.UiSettingAddressPtr;

            var offsetResult = m_ProcessInfo.Scanner.CompiledFindPattern("40 84 ed 0f 94 05");
            var resultRelativeAddress = IntPtr.Add(IntPtr.Add(m_ProcessInfo.BaseAddress, offsetResult.Offset), 6);
            var offSetAddress = WindowsHelper.Read<int>(m_ProcessInfo.Handle, resultRelativeAddress);

            return IntPtr.Add(m_ProcessInfo.BaseAddress, offsetResult.Offset + 9 + offSetAddress);
        }

        public bool CurrentPlayerValid() {
            var unitAny = WindowsHelper.Read<UnitAny>(m_ProcessInfo.Handle, _mMemoryState.PlayerUnitPtr);
            return unitAny.Inventory != IntPtr.Zero;
        }

        // Logic took from https://github.com/RushTheOne/MapAssist/blob/main/Helpers/GameMemory.cs
        public unsafe GameState? GetState() {
            try {
                m_ProcessInfo = GetProcessInfo();
                if (m_ProcessInfo == null) {
                    Reset();
                    return null;
                }

                if (_mMemoryState.PlayerUnitPtr == IntPtr.Zero || !CurrentPlayerValid()) {
                    var unitHashTablePtr = GetUnitHashtableAddress();
                    var uiSettingPtr = GetUiSettingAddress();

                    var result = FindPlayer(unitHashTablePtr, m_ProcessInfo);
                    if (!result.HasValue)
                        return null;

                    _mMemoryState = new MemoryState() {
                        PlayerUnit = result.Value.data,
                        PlayerUnitPtr = result.Value.ptr,
                        UnitHashtableAddressPtr = unitHashTablePtr,
                        UiSettingAddressPtr = uiSettingPtr,
                        GameSocket = GetGameSocket()
                    };
                }

                mGameMenus = WindowsHelper.Read<UiGameMenus>(m_ProcessInfo.Handle, _mMemoryState.UiSettingAddressPtr);
                mPath = ReadAndValidate<Models.Path>(m_ProcessInfo, _mMemoryState.PlayerUnit.pPath);
                mRoom = ReadAndValidate<Room>(m_ProcessInfo, mPath.pRoom);
                mRoomEx = ReadAndValidate<RoomEx>(m_ProcessInfo, mRoom.pRoomEx);
                mLevel = ReadAndValidate<Level>(m_ProcessInfo, mRoomEx.pLevel);
                mStatList = ReadAndValidate<StatList>(m_ProcessInfo, _mMemoryState.PlayerUnit.StatsList);

                var playerName = Encoding.ASCII.GetString(WindowsHelper.Read<byte>(m_ProcessInfo.Handle, _mMemoryState.PlayerUnit.UnitData, 16)).TrimEnd((char)0);
                var stats = WindowsHelper.Read<StatValue>(m_ProcessInfo.Handle, mStatList.Stats2.Array, (int)mStatList.Stats2.Size);
                var levelId = mLevel.LevelId;
                if (levelId == Area.None)
                    throw new Exception("Invalid area");

                var currentState = new GameState {
                    PlayerName = playerName,
                    CurrentArea = mLevel.LevelId,
                    PreviousHealth = previousState?.CurrentHealth,
                    CurrentHealth = GetCurrentHealth(stats),
                    MaxHealth = GetMaxHp(stats),
                    IsInTown =
                        levelId is Area.RogueEncampment or Area.LutGholein or Area.KurastDocks or Area
                            .ThePandemoniumFortress or Area.Harrogath,
                    IsGameMenuOpen = mGameMenus.IsGameMenuOpen == 1,
                    WindowHandle = m_ProcessInfo.ProcessWindowHandle,
                    GameSocket = _mMemoryState.GameSocket
                };
                previousState = currentState;
                return currentState;
            } catch (Exception) {
                Reset();
                return null;
            }
        }

        static (IntPtr ptr, UnitAny data)? FindPlayer(IntPtr unitHashTablePtr, ProcessInfo pInfo) {
            var dic = new Dictionary<string, List<(IntPtr ptr, UnitAny data)>>();
            var mainPlayerName = string.Empty;
            var mainPlayerPtr = IntPtr.Zero;
            var unitHashTable = WindowsHelper.Read<UnitHashTable>(pInfo.Handle, unitHashTablePtr);

            foreach (var unitPtr in unitHashTable.UnitTable) {
                var unitAny = WindowsHelper.Read<UnitAny>(pInfo.Handle, unitPtr);
                ReadAllUnits(unitAny, unitPtr, dic, pInfo.Handle, ref mainPlayerName);
            }
            foreach (var ptrAndData in dic[mainPlayerName]) {
                var path = WindowsHelper.Read<Models.Path>(pInfo.Handle, ptrAndData.data.pPath);
                if (path.DynamicY != 0 && path.DynamicY != 0) {
                    return ptrAndData;
                }
            }

            return null;

        }

        public static void ReadAllUnits(UnitAny unitAny, IntPtr curUnitPtr, Dictionary<string, List<(IntPtr ptr, UnitAny data)>> dic, IntPtr hdl, ref string mainPlayer) {
            if (curUnitPtr == IntPtr.Zero)
                return;

            if(unitAny.UnityType == 0) {
                var playerName = Encoding.ASCII.GetString(WindowsHelper.Read<byte>(hdl, unitAny.UnitData, 16)).TrimEnd((char)0);
                if (!string.IsNullOrEmpty(playerName)) {
                    if (dic.ContainsKey(playerName)) {
                        dic[playerName].Add((curUnitPtr, unitAny));
                        mainPlayer = playerName;
                    } else {
                        dic.Add(playerName, new List<(IntPtr, UnitAny)> { (curUnitPtr, unitAny) });
                    }
                }
            }
            ReadAllUnits(WindowsHelper.Read<UnitAny>(hdl, unitAny.pListNext), unitAny.pListNext, dic, hdl, ref mainPlayer);
        }

        static int GetCurrentHealth(StatValue[] stats) => stats.FirstOrDefault(x => x.Stat == Stat.STAT_HP).Value >> 8;
        static int GetMaxHp(StatValue[] stats) => stats.FirstOrDefault(x => x.Stat == Stat.STAT_MAXHP).Value >> 8;

        private T ReadAndValidate<T>(ProcessInfo pInfo, IntPtr ptr) where T : struct {
            if (ptr == IntPtr.Zero)
                throw new Exception($"Pointer to {typeof(T).Name} is null");
            return WindowsHelper.Read<T>(pInfo.Handle, ptr);
        }

        /*
            This is best effort to find the game socket. We take the most recent one. 
        */
        public InteropTcpHelper.MIB_TCPROW_OWNER_PID GetGameSocket() {
            if (this._mMemoryState.GameSocket.HasValue)
                return this._mMemoryState.GameSocket.Value;
            var d2rSockets = InteropTcpHelper.GetAllTcpConnections().Where(x =>
                    x.state == (uint)InteropTcpHelper.State.Established &&
                    x.owningPid == m_ProcessInfo.Process.Id
            ).ToList();

            var seen = new HashSet<uint>();
            var duplicate = new HashSet<uint>();
            foreach (var s in d2rSockets) {
                if (seen.Contains(s.remoteAddr))
                    duplicate.Add(s.remoteAddr);
                seen.Add(s.remoteAddr);
            }
            d2rSockets = d2rSockets.Where(x => !duplicate.Contains(x.remoteAddr)).ToList();
            var socketForThatIp = d2rSockets.Last();
            this._mMemoryState.GameSocket = socketForThatIp;
            Logger.LogInformation("found ip: {gameIp}", IPIntToString((int)socketForThatIp.remoteAddr));

            return socketForThatIp;
        }

        public static string IPIntToString(int IP) {
            byte[] addr = System.BitConverter.GetBytes(IP);
            return addr[0] + "." + addr[1] + "." + addr[2] + "." + addr[3];
        }
    }
}
