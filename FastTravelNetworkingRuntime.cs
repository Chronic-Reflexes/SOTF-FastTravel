using System;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace FastTravel
{
    internal sealed class FastTravelNetworkingRuntime : MonoBehaviour
    {
        private const float AuthorityRefreshIntervalSeconds = 1.5f;

        private static bool _installed;
        private static Type _boltNetworkType;
        private static Type _netUtilsType;
        private static bool? _lastAuthorityState;
        private static GameObject _runtimeHost;
        private static FastTravelNetworkingRuntime _runtimeInstance;

        private float _nextRefreshAt;

        public FastTravelNetworkingRuntime(IntPtr ptr) : base(ptr)
        {
        }

        public static void Install()
        {
            if (_runtimeInstance != null)
                return;

            if (_runtimeHost != null)
            {
                var existing = _runtimeHost.GetComponent<FastTravelNetworkingRuntime>();
                if (existing != null)
                {
                    _runtimeInstance = existing;
                    _installed = true;
                    return;
                }

                _runtimeHost = null;
                _installed = false;
            }

            if (_installed)
                _installed = false;

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<FastTravelNetworkingRuntime>();
            }
            catch
            {
                // Already registered or unavailable on this runtime.
            }

            try
            {
                var host = new GameObject("FastTravelNetworkingRuntime");
                UnityEngine.Object.DontDestroyOnLoad(host);
                var instance = host.AddComponent<FastTravelNetworkingRuntime>();
                _runtimeHost = host;
                _runtimeInstance = instance;
                _installed = instance != null;
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Failed to install network runtime bridge: " + ex.Message);
            }
        }

        internal static bool TryGetLocalPlayerIdentity(out string playerId, out string playerName)
        {
            playerId = null;
            playerName = null;

            Type boltType = ResolveBoltNetworkType();
            if (boltType == null)
                return false;

            if (TryReadStaticMemberValue(boltType, new[] { "localPlayer", "LocalPlayer", "Client", "client" }, out object localPlayer) && localPlayer != null)
            {
                if (TryReadMemberAsString(localPlayer, new[] { "SteamId", "steamId", "PlayerId", "playerId", "UserId", "userId", "ActorNumber", "actorNumber", "Id", "id" }, out string localId)
                    && !string.IsNullOrEmpty(localId))
                {
                    playerId = "bolt:" + localId;
                }

                if (TryReadMemberAsString(localPlayer, new[] { "DisplayName", "displayName", "PlayerName", "playerName", "Name", "name" }, out string localName)
                    && !string.IsNullOrEmpty(localName))
                {
                    playerName = localName;
                }
            }

            if (string.IsNullOrEmpty(playerId)
                && TryReadStaticMemberAsString(boltType, new[] { "localPlayerId", "LocalPlayerId", "SteamId", "steamId" }, out string staticId)
                && !string.IsNullOrEmpty(staticId))
            {
                playerId = "bolt:" + staticId;
            }

            if (string.IsNullOrEmpty(playerName))
            {
                TryReadStaticMemberAsString(boltType, new[] { "localPlayerName", "LocalPlayerName", "PlayerName", "playerName", "Name", "name" }, out playerName);
            }

            return !string.IsNullOrEmpty(playerId) || !string.IsNullOrEmpty(playerName);
        }

        private void Awake()
        {
            RefreshAuthority(logOnlyOnChange: false);
            FastTravelPublicBedNetworkSync.Tick();
            _nextRefreshAt = Time.unscaledTime + AuthorityRefreshIntervalSeconds;
        }

        private void Update()
        {
            FastTravelPublicBedNetworkSync.Tick();

            if (Time.unscaledTime < _nextRefreshAt)
                return;

            _nextRefreshAt = Time.unscaledTime + AuthorityRefreshIntervalSeconds;
            RefreshAuthority(logOnlyOnChange: true);
        }

        private void OnDestroy()
        {
            if (_runtimeInstance == this)
                _runtimeInstance = null;

            if (_runtimeHost != null && _runtimeHost == gameObject)
                _runtimeHost = null;

            _installed = false;
            FastTravelPublicBedNetworkSync.Shutdown();
        }

        private static void RefreshAuthority(bool logOnlyOnChange)
        {
            bool isAuthority = DetermineLocalAuthority(out string mode);
            FastTravelLocationRegistry.SetLocalServerAuthority(isAuthority);

            if (!logOnlyOnChange || _lastAuthorityState != isAuthority)
            {
                _lastAuthorityState = isAuthority;
                ModMain.LogMessage("FastTravel: Network authority mode -> " + (isAuthority ? "host/server" : "client") + " (" + mode + ").");
            }
        }

        private static bool DetermineLocalAuthority(out string mode)
        {
            if (TryReadNetUtilsMemberAsBool("IsServer", out bool netUtilsServer) && netUtilsServer)
            {
                mode = "netutils-server";
                return true;
            }

            if (TryReadNetUtilsMemberAsBool("IsDedicatedServer", out bool netUtilsDedicatedServer) && netUtilsDedicatedServer)
            {
                mode = "netutils-dedicated-server";
                return true;
            }

            if (TryReadNetUtilsMemberAsBool("IsClient", out bool netUtilsClient) && netUtilsClient)
            {
                mode = "netutils-client";
                return false;
            }

            if (TryReadNetUtilsMemberAsBool("IsMultiplayer", out bool netUtilsMultiplayer) && netUtilsMultiplayer)
            {
                mode = "netutils-multiplayer";
                return false;
            }

            if (TryGetBoltRuntimeState(out bool boltRunning, out bool boltServer, out bool boltClient, out bool boltDedicatedServer, out bool boltStateRead))
            {
                if (boltServer || boltDedicatedServer)
                {
                    mode = boltDedicatedServer ? "bolt-dedicated-server" : "bolt-server";
                    return true;
                }

                if (boltClient)
                {
                    mode = "bolt-client";
                    return false;
                }

                if (boltRunning)
                {
                    mode = "bolt-running-unknown";
                    return false;
                }

                if (boltStateRead)
                {
                    mode = "bolt-idle";
                    return true;
                }
            }

            mode = "network-state-unknown";
            return true;
        }

        internal static bool TryGetRuntimeNetworkState(out bool isMultiplayer, out bool isServer, out bool isClient, out bool isDedicatedServer)
        {
            isMultiplayer = false;
            isServer = false;
            isClient = false;
            isDedicatedServer = false;

            bool foundAny = false;

            if (TryReadNetUtilsMemberAsBool("IsMultiplayer", out bool netUtilsMultiplayer))
            {
                isMultiplayer = netUtilsMultiplayer;
                foundAny = true;
            }

            if (TryReadNetUtilsMemberAsBool("IsServer", out bool netUtilsServer))
            {
                isServer = netUtilsServer;
                foundAny = true;
            }

            if (TryReadNetUtilsMemberAsBool("IsClient", out bool netUtilsClient))
            {
                isClient = netUtilsClient;
                foundAny = true;
            }

            if (TryReadNetUtilsMemberAsBool("IsDedicatedServer", out bool netUtilsDedicatedServer))
            {
                isDedicatedServer = netUtilsDedicatedServer;
                foundAny = true;
            }

            if (TryGetBoltRuntimeState(out bool boltRunning, out bool boltServer, out bool boltClient, out bool boltDedicatedServer, out bool boltStateRead)
                && boltStateRead)
            {
                foundAny = true;
                isServer = isServer || boltServer || boltDedicatedServer;
                isDedicatedServer = isDedicatedServer || boltDedicatedServer;
                isClient = isClient || boltClient;

                bool boltMultiplayer = boltRunning || boltServer || boltClient || boltDedicatedServer;
                isMultiplayer = isMultiplayer || boltMultiplayer;
            }

            return foundAny;
        }

        private static Type ResolveNetUtilsType()
        {
            if (_netUtilsType != null)
                return _netUtilsType;

            string[] candidates =
            {
                "SonsSdk.Networking.NetUtils, SonsSdk",
                "SonsSdk.Networking.NetUtils"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                Type type = Type.GetType(candidates[i], throwOnError: false);
                if (type != null)
                {
                    _netUtilsType = type;
                    return _netUtilsType;
                }
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                if (asm == null)
                    continue;

                Type type = asm.GetType("SonsSdk.Networking.NetUtils", throwOnError: false, ignoreCase: false);
                if (type != null)
                {
                    _netUtilsType = type;
                    return _netUtilsType;
                }
            }

            return null;
        }

        private static bool TryReadNetUtilsMemberAsBool(string memberName, out bool value)
        {
            value = false;
            Type netUtilsType = ResolveNetUtilsType();
            if (netUtilsType == null)
                return false;

            return TryReadStaticMemberAsBool(netUtilsType, new[] { memberName }, out value);
        }

        private static bool TryGetBoltRuntimeState(out bool isRunning, out bool isServer, out bool isClient, out bool isDedicatedServer, out bool readAny)
        {
            isRunning = false;
            isServer = false;
            isClient = false;
            isDedicatedServer = false;
            readAny = false;

            Type boltType = ResolveBoltNetworkType();
            if (boltType == null)
                return false;

            if (TryReadStaticMemberAsBool(boltType, new[] { "isRunning", "IsRunning", "running", "Running", "isConnected", "IsConnected" }, out bool boltRunning))
            {
                isRunning = boltRunning;
                readAny = true;
            }

            if (TryReadStaticMemberAsBool(boltType, new[] { "isServer", "IsServer", "isHost", "IsHost", "isServerOrSinglePlayer", "IsServerOrSinglePlayer" }, out bool boltServer))
            {
                isServer = boltServer;
                readAny = true;
            }

            if (TryReadStaticMemberAsBool(boltType, new[] { "isClient", "IsClient", "isClientOnly", "IsClientOnly" }, out bool boltClient))
            {
                isClient = boltClient;
                readAny = true;
            }

            if (TryReadStaticMemberAsBool(boltType, new[] { "isDedicatedServer", "IsDedicatedServer" }, out bool boltDedicatedServer))
            {
                isDedicatedServer = boltDedicatedServer;
                readAny = true;
            }

            return true;
        }

        private static Type ResolveBoltNetworkType()
        {
            if (_boltNetworkType != null)
                return _boltNetworkType;

            string[] candidates =
            {
                "Bolt.BoltNetwork, Assembly-CSharp",
                "Bolt.BoltNetwork",
                "Photon.Bolt.BoltNetwork, Assembly-CSharp",
                "Photon.Bolt.BoltNetwork"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                var type = Type.GetType(candidates[i], throwOnError: false);
                if (type != null)
                {
                    _boltNetworkType = type;
                    ModMain.LogMessage("FastTravel: Bolt runtime detected via '" + candidates[i] + "'.");
                    return _boltNetworkType;
                }
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                Type[] types = GetLoadableTypes(asm);
                for (int j = 0; j < types.Length; j++)
                {
                    var type = types[j];
                    if (type == null)
                        continue;

                    string fullName = type.FullName ?? string.Empty;
                    if (string.Equals(type.Name, "BoltNetwork", StringComparison.OrdinalIgnoreCase)
                        || fullName.EndsWith(".BoltNetwork", StringComparison.OrdinalIgnoreCase))
                    {
                        _boltNetworkType = type;
                        ModMain.LogMessage("FastTravel: Bolt runtime detected via loaded type '" + fullName + "'.");
                        return _boltNetworkType;
                    }
                }
            }

            ModMain.LogMessage("FastTravel: Bolt runtime type not found; defaulting to local authority mode.");
            return null;
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            if (assembly == null)
                return Array.Empty<Type>();

            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types ?? Array.Empty<Type>();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static bool TryReadStaticMemberAsString(Type type, string[] memberNames, out string value)
        {
            value = null;
            if (!TryReadStaticMemberValue(type, memberNames, out object raw))
                return false;

            value = ConvertMemberValueToString(raw);
            return !string.IsNullOrEmpty(value);
        }

        private static bool TryReadStaticMemberAsBool(Type type, string[] memberNames, out bool value)
        {
            value = false;
            if (!TryReadStaticMemberValue(type, memberNames, out object raw))
                return false;

            return TryConvertToBool(raw, out value);
        }

        private static bool TryReadStaticMemberValue(Type type, string[] memberNames, out object value)
        {
            value = null;
            if (type == null || memberNames == null)
                return false;

            for (int i = 0; i < memberNames.Length; i++)
            {
                string name = memberNames[i];
                if (string.IsNullOrEmpty(name))
                    continue;

                if (TryReadStaticMemberValue(type, name, out value))
                    return true;
            }

            return false;
        }

        private static bool TryReadStaticMemberValue(Type type, string memberName, out object value)
        {
            value = null;
            if (type == null || string.IsNullOrEmpty(memberName))
                return false;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.IgnoreCase;

            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(null, null);
                    return true;
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    value = field.GetValue(null);
                    return true;
                }

                var getter = type.GetMethod("get_" + memberName, flags, null, Type.EmptyTypes, null);
                if (getter != null)
                {
                    value = getter.Invoke(null, null);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadMemberAsString(object source, string[] memberNames, out string value)
        {
            value = null;
            if (source == null || memberNames == null)
                return false;

            for (int i = 0; i < memberNames.Length; i++)
            {
                if (TryReadMemberAsString(source, memberNames[i], out value))
                    return true;
            }

            return false;
        }

        private static bool TryReadMemberAsString(object source, string memberName, out string value)
        {
            value = null;
            if (!TryReadMemberValue(source, memberName, out object raw))
                return false;

            value = ConvertMemberValueToString(raw);
            return !string.IsNullOrEmpty(value);
        }

        private static bool TryReadMemberValue(object source, string memberName, out object value)
        {
            value = null;
            if (source == null || string.IsNullOrEmpty(memberName))
                return false;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var type = source.GetType();

            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(source, null);
                    return true;
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    value = field.GetValue(source);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryConvertToBool(object value, out bool result)
        {
            result = false;
            if (value == null)
                return false;

            if (value is bool boolValue)
            {
                result = boolValue;
                return true;
            }

            if (value is int intValue)
            {
                result = intValue != 0;
                return true;
            }

            if (value is string str)
            {
                if (bool.TryParse(str, out bool parsedBool))
                {
                    result = parsedBool;
                    return true;
                }

                if (int.TryParse(str, out int parsedInt))
                {
                    result = parsedInt != 0;
                    return true;
                }
            }

            return false;
        }

        private static string ConvertMemberValueToString(object value)
        {
            if (value == null)
                return null;

            if (value is string str)
            {
                string trimmed = str.Trim();
                return trimmed.Length > 0 ? trimmed : null;
            }

            if (value is int || value is uint || value is long || value is ulong || value is short || value is ushort || value is byte || value is sbyte)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

            if (value is Enum)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

            return null;
        }
    }
}
