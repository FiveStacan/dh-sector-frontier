using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.DeviceLinking.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Shared.DeviceLinking;
using Content.Shared.Access.Systems;
using Content.Shared.Popups;
using Content.Shared.Research.Components;
using Content.Shared.Research.Systems;
using Content.Server.GameTicking.Events; // Dark Haven - persistence re-link at round start
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Research.Systems
{
    [UsedImplicitly]
    public sealed partial class ResearchSystem : SharedResearchSystem
    {
        [Dependency] private readonly IAdminLogManager _adminLog = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly AccessReaderSystem _accessReader = default!;
        [Dependency] private readonly EntityLookupSystem _lookup = default!;
        [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
        [Dependency] private readonly SharedPopupSystem _popup = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;
        [Dependency] private readonly DeviceLinkSystem _deviceLink = default!;
        // [Dependency] private readonly RadioSystem _radio = default!; // Frontier

        private const string ResearchServerLinkPort = "ResearchServerSender";
        private const string ResearchConsoleLinkPort = "ResearchConsoleReceiver";

        private readonly HashSet<Entity<ResearchServerComponent>> ClientLookup = new(); // Frontier: not static

        public override void Initialize()
        {
            base.Initialize();
            InitializeClient();
            InitializeConsole();
            InitializeSource();
            InitializeServer();

            SubscribeLocalEvent<TechnologyDatabaseComponent, ResearchRegistrationChangedEvent>(OnDatabaseRegistrationChanged);

            SubscribeLocalEvent<RoundStartingEvent>(OnRoundStartingReconcile); // Dark Haven - persistence re-link
        }

        /// <summary>
        /// Dark Haven - persistence: research clients loaded from a map save are already MapInitialized, so
        /// OnClientMapInit is not re-raised and they reload with no server link (points/tech sharing stop). At
        /// round start (maps fully loaded and initialized, broadphase ready) re-link any client that has no
        /// server to a co-grid server. RegisterClient enforces the grid match and guards duplicates, so
        /// already-linked (freshly generated) clients are skipped.
        /// </summary>
        private void OnRoundStartingReconcile(RoundStartingEvent ev)
        {
            var query = EntityQueryEnumerator<ResearchClientComponent>();
            while (query.MoveNext(out var uid, out var client))
            {
                if (client.Server != null)
                    continue;

                var servers = GetServers(uid);
                if (servers.Count == 0)
                    continue;

                var server = servers.First();
                RegisterClient(uid, server.Owner, client, server.Comp);
            }
        }

        /// <summary>
        /// Gets a server based on its unique numeric id.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="id"></param>
        /// <param name="serverUid"></param>
        /// <param name="serverComponent"></param>
        /// <returns></returns>
        public bool TryGetServerById(EntityUid client, int id, [NotNullWhen(true)] out EntityUid? serverUid, [NotNullWhen(true)] out ResearchServerComponent? serverComponent)
        {
            serverUid = null;
            serverComponent = null;

            var query = GetServers(client).ToList();
            foreach (var (uid, server) in query)
            {
                if (server.Id != id)
                    continue;
                serverUid = uid;
                serverComponent = server;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the names of all the servers.
        /// </summary>
        /// <returns></returns>
        public string[] GetServerNames(EntityUid client)
        {
            var allServers = GetServers(client).ToArray();
            var list = new string[allServers.Length];

            for (var i = 0; i < allServers.Length; i++)
            {
                list[i] = allServers[i].Comp.ServerName;
            }

            return list;
        }

        /// <summary>
        /// Gets the ids of all the servers
        /// </summary>
        /// <returns></returns>
        public int[] GetServerIds(EntityUid client)
        {
            var allServers = GetServers(client).ToArray();
            var list = new int[allServers.Length];

            for (var i = 0; i < allServers.Length; i++)
            {
                list[i] = allServers[i].Comp.Id;
            }

            return list;
        }

        public HashSet<Entity<ResearchServerComponent>> GetServers(EntityUid client)
        {
            ClientLookup.Clear();

            if (!TryComp(client, out ResearchClientComponent? clientComponent))
                return ClientLookup;

            var clientXform = Transform(client);
            if (clientXform.GridUid is { } grid)
            {
                _lookup.GetGridEntities(grid, ClientLookup);
                ClientLookup.RemoveWhere(server =>
                    server.Comp.GridLocked || !IsClientServerTypeCompatible(clientComponent, server.Comp));
            }

            if (HasComp<DeviceLinkSinkComponent>(client))
            {
                var query = AllEntityQuery<ResearchServerComponent>();
                while (query.MoveNext(out var serverUid, out var serverComponent))
                {
                    if (!IsClientServerTypeCompatible(clientComponent, serverComponent)
                        || !IsNetworkLinkedServerAvailable(client, serverUid, serverComponent))
                        continue;

                    ClientLookup.Add((serverUid, serverComponent));
                }
            }

            return ClientLookup;
        }

        private bool CanClientAccessServer(EntityUid client, EntityUid server, ResearchClientComponent clientComponent,
            ResearchServerComponent serverComponent)
        {
            if (!IsClientServerTypeCompatible(clientComponent, serverComponent))
                return false;

            if (IsSameGridServerAvailable(client, server, serverComponent))
                return true;

            return IsNetworkLinkedServerAvailable(client, server, serverComponent);
        }

        private bool IsSameGridServerAvailable(EntityUid client, EntityUid server, ResearchServerComponent serverComponent)
        {
            if (serverComponent.GridLocked)
                return false;

            if (!TryComp(client, out TransformComponent? clientXform)
                || !TryComp(server, out TransformComponent? serverXform)
                || clientXform.GridUid == null)
                return false;

            return clientXform.GridUid == serverXform.GridUid;
        }

        private bool IsNetworkLinkedServerAvailable(EntityUid client, EntityUid server, ResearchServerComponent serverComponent)
        {
            if (!CanRun(server))
                return false;

            if (!TryComp(server, out DeviceLinkSourceComponent? source)
                || !HasComp<DeviceLinkSinkComponent>(client))
            {
                return false;
            }

            var links = _deviceLink.GetLinks(server, client, source);
            if (!links.Contains((ResearchServerLinkPort, ResearchConsoleLinkPort)))
                return false;

            return _transform.GetMapCoordinates(server).InRange(_transform.GetMapCoordinates(client), serverComponent.NetworkLinkRange);
        }

        private static bool IsClientServerTypeCompatible(ResearchClientComponent client, ResearchServerComponent server)
        {
            if (client.AllowedFactions.Count == 0)
                return true;

            return client.AllowedFactions.Any(faction => faction == server.Faction);
        }

        public override void Update(float frameTime)
        {
            var query = EntityQueryEnumerator<ResearchServerComponent>();
            while (query.MoveNext(out var uid, out var server))
            {
                if (server.NextUpdateTime > _timing.CurTime)
                    continue;
                server.NextUpdateTime = _timing.CurTime + server.ResearchConsoleUpdateTime;

                UpdateServer(uid, (int) server.ResearchConsoleUpdateTime.TotalSeconds, server);
                ValidateServerClients(uid, server);
            }
        }
    }
}
