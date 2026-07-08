using System.Linq;
using Content.Server.Power.EntitySystems;
using Content.Shared.DeviceLinking;
using Content.Shared.Research.Components;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Server.Research.Systems;

public sealed partial class ResearchSystem
{
    private void InitializeServer()
    {
        SubscribeLocalEvent<ResearchServerComponent, ComponentStartup>(OnServerStartup);
        SubscribeLocalEvent<ResearchServerComponent, ComponentShutdown>(OnServerShutdown);
        SubscribeLocalEvent<ResearchServerComponent, TechnologyDatabaseModifiedEvent>(OnServerDatabaseModified);
        SubscribeLocalEvent<ResearchServerComponent, GetVerbsEvent<AlternativeVerb>>(OnServerGetVerbs);
        SubscribeLocalEvent<ResearchServerComponent, AnchorStateChangedEvent>(OnServerAnchorChanged); // Frontier
        SubscribeLocalEvent<ResearchServerComponent, EntParentChangedMessage>(OnServerParentChanged); // Frontier
    }

    private void OnServerStartup(EntityUid uid, ResearchServerComponent component, ComponentStartup args)
    {
        var unusedId = EntityQuery<ResearchServerComponent>(true)
            .Max(s => s.Id) + 1;
        component.Id = unusedId;
        Dirty(uid, component);
    }

    private void OnServerShutdown(EntityUid uid, ResearchServerComponent component, ComponentShutdown args)
    {
        foreach (var client in new List<EntityUid>(component.Clients))
        {
            UnregisterClient(client, uid, serverComponent: component, dirtyServer: false);
        }
    }

    private void OnServerDatabaseModified(EntityUid uid, ResearchServerComponent component, ref TechnologyDatabaseModifiedEvent args)
    {
        foreach (var client in component.Clients)
        {
            RaiseLocalEvent(client, ref args);
        }
    }

    private void OnServerGetVerbs(Entity<ResearchServerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString(ent.Comp.GridLocked
                ? "research-server-verb-unlock-grid"
                : "research-server-verb-lock-grid"),
            Icon = new SpriteSpecifier.Texture(new ResPath(ent.Comp.GridLocked
                ? "/Textures/Interface/VerbIcons/unlock.svg.192dpi.png"
                : "/Textures/Interface/VerbIcons/lock.svg.192dpi.png")),
            Priority = 1,
            Act = () => ToggleServerGridLock(ent)
        });

        if (TryComp<DeviceLinkSourceComponent>(ent, out var source) && source.LinkedPorts.Count > 0)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("research-server-verb-reset-console-links"),
                Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/refresh.svg.192dpi.png")),
                Priority = 0,
                Act = () => ResetServerConsoleLinks(ent, source)
            });
        }
    }

    private void ToggleServerGridLock(Entity<ResearchServerComponent> ent)
    {
        ent.Comp.GridLocked = !ent.Comp.GridLocked;
        Dirty(ent);

        ValidateServerClients(ent, ent.Comp);
        _popup.PopupEntity(Loc.GetString(ent.Comp.GridLocked
            ? "research-server-grid-locked"
            : "research-server-grid-unlocked"), ent);
    }

    private void ResetServerConsoleLinks(Entity<ResearchServerComponent> ent, DeviceLinkSourceComponent? source = null)
    {
        if (!Resolve(ent, ref source, false))
            return;

        foreach (var sink in source.LinkedPorts.Keys.ToArray())
        {
            _deviceLink.RemoveSinkFromSource(ent, sink, source);
        }

        ValidateServerClients(ent, ent.Comp);
        Dirty(ent, source);
        _popup.PopupEntity(Loc.GetString("research-server-console-links-reset"), ent);
    }

    private bool CanRun(EntityUid uid)
    {
        return this.IsPowered(uid, EntityManager);
    }

    private void UpdateServer(EntityUid uid, int time, ResearchServerComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!CanRun(uid))
            return;
        ModifyServerPoints(uid, GetPointsPerSecond(uid, component) * time, component);
    }

    private void ValidateServerClients(EntityUid uid, ResearchServerComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        var clientsRemoved = false;
        foreach (var client in component.Clients.ToArray())
        {
            if (!TryComp(client, out ResearchClientComponent? clientComponent))
            {
                component.Clients.Remove(client);
                clientsRemoved = true;
                continue;
            }

            if (!CanClientAccessServer(client, uid, clientComponent, component))
            {
                UnregisterClient(client, uid, clientComponent, component, dirtyServer: false);
                clientsRemoved = true;
            }
        }

        if (clientsRemoved && !TerminatingOrDeleted(uid))
            Dirty(uid, component);
    }

    /// <summary>
    /// Registers a client to the specified server.
    /// </summary>
    /// <param name="client">The client being registered</param>
    /// <param name="server">The server the client is being registered to</param>
    /// <param name="clientComponent"></param>
    /// <param name="serverComponent"></param>
    /// <param name="dirtyServer">Whether or not to dirty the server component after registration</param>
    public void RegisterClient(EntityUid client, EntityUid server, ResearchClientComponent? clientComponent = null,
        ResearchServerComponent? serverComponent = null, bool dirtyServer = true)
    {
        if (!Resolve(client, ref clientComponent, false) || !Resolve(server, ref serverComponent, false))
            return;

        if (serverComponent.Clients.Contains(client))
            return;
        // Frontier: check grids or explicit device links
        if (!CanClientAccessServer(client, server, clientComponent, serverComponent))
            return;
        // End Frontier

        serverComponent.Clients.Add(client);
        clientComponent.Server = server;
        SyncClientWithServer(client, clientComponent: clientComponent);

        if (dirtyServer && !TerminatingOrDeleted(server))
            Dirty(server, serverComponent);

        var ev = new ResearchRegistrationChangedEvent(server);
        RaiseLocalEvent(client, ref ev);
    }

    /// <summary>
    /// Unregisters a client from its server
    /// </summary>
    /// <param name="client"></param>
    /// <param name="clientComponent"></param>
    /// <param name="dirtyServer"></param>
    public void UnregisterClient(EntityUid client, ResearchClientComponent? clientComponent = null, bool dirtyServer = true)
    {
        if (!Resolve(client, ref clientComponent))
            return;

        if (clientComponent.Server is not { } server)
            return;

        UnregisterClient(client, server, clientComponent, dirtyServer: dirtyServer);
    }

    /// <summary>
    /// Unregisters a client from its server
    /// </summary>
    /// <param name="client"></param>
    /// <param name="server"></param>
    /// <param name="clientComponent"></param>
    /// <param name="serverComponent"></param>
    /// <param name="dirtyServer"></param>
    public void UnregisterClient(EntityUid client, EntityUid server, ResearchClientComponent? clientComponent = null,
        ResearchServerComponent? serverComponent = null, bool dirtyServer = true)
    {
        if (!Resolve(client, ref clientComponent, false) || !Resolve(server, ref serverComponent, false))
            return;

        serverComponent.Clients.Remove(client);
        clientComponent.Server = null;
        SyncClientWithServer(client, clientComponent: clientComponent);

        if (dirtyServer && !TerminatingOrDeleted(server))
        {
            Dirty(server, serverComponent);
        }

        var ev = new ResearchRegistrationChangedEvent(null);
        RaiseLocalEvent(client, ref ev);
    }

    /// <summary>
    /// Gets the amount of points generated by all the server's sources in a second.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="component"></param>
    /// <returns></returns>
    public int GetPointsPerSecond(EntityUid uid, ResearchServerComponent? component = null)
    {
        var points = 0;

        if (!Resolve(uid, ref component))
            return points;

        if (!CanRun(uid))
            return points;

        var ev = new ResearchServerGetPointsPerSecondEvent(uid, points);
        foreach (var client in component.Clients)
        {
            RaiseLocalEvent(client, ref ev);
        }
        return ev.Points;
    }

    /// <summary>
    /// Adds a specified number of points to a server.
    /// </summary>
    /// <param name="uid">The server</param>
    /// <param name="points">The amount of points being added</param>
    /// <param name="component"></param>
    public void ModifyServerPoints(EntityUid uid, int points, ResearchServerComponent? component = null)
    {
        if (points == 0)
            return;

        if (!Resolve(uid, ref component))
            return;
        component.Points += points;
        var ev = new ResearchServerPointsChangedEvent(uid, component.Points, points);
        foreach (var client in component.Clients)
        {
            RaiseLocalEvent(client, ref ev);
        }
        Dirty(uid, component);
    }

    // Frontier: unanchoring server
    private void OnServerAnchorChanged(Entity<ResearchServerComponent> ent, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored || ent.Comp.Clients.Count <= 0)
            return;

        // Server yanked, unregister the clients.
        var clientList = new List<EntityUid>(ent.Comp.Clients);
        bool clientsRemoved = false;
        foreach (var client in clientList)
        {
            UnregisterClient(client, ent, serverComponent: ent.Comp, dirtyServer: false);
            clientsRemoved = true;
        }

        if (clientsRemoved)
            Dirty(ent);
    }

    private void OnServerParentChanged(Entity<ResearchServerComponent> ent, ref EntParentChangedMessage args)
    {
        if (TerminatingOrDeleted(ent))
            return;

        ValidateServerClients(ent, ent.Comp);
    }
    // End Frontier
}
