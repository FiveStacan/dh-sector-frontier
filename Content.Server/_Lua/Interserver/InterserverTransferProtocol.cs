using System.Linq;

namespace Content.Server._Lua.Interserver;

/// <summary>Protocol invariants shared by HTTP handling, recovery code and tests.</summary>
public static class InterserverTransferProtocol
{
    public static bool MatchesReservation(
        InterserverTransferRecord record,
        InterserverReserveRequest request,
        string authenticatedPeerId)
    {
        return record.TransferId == request.TransferId &&
               record.ShipId == request.ShipId &&
               record.SourceServerId == authenticatedPeerId &&
               record.DestinationMapId == request.DestinationMapId &&
               record.OwnershipEpoch == request.OwnershipEpoch;
    }

    public static bool IsTerminal(InterserverJournalStage stage) =>
        stage is InterserverJournalStage.Completed or InterserverJournalStage.Aborted or InterserverJournalStage.Failed;

    /// <summary>
    /// These responses prove that the destination did not take ownership. Committing is intentionally excluded:
    /// after an ambiguous HTTP timeout it must be polled until it becomes committed or explicitly fails.
    /// </summary>
    public static bool ProvesDestinationDidNotCommit(InterserverTransferResponse? response)
    {
        if (response == null)
            return false;

        return response.Stage is nameof(InterserverJournalStage.Reserved)
            or nameof(InterserverJournalStage.Uploaded)
            or nameof(InterserverJournalStage.Aborted)
            or nameof(InterserverJournalStage.Failed)
            or "Missing";
    }

    public static bool IsDestinationCommitted(InterserverTransferResponse? response) =>
        response is { Success: true } &&
        response.Stage is nameof(InterserverJournalStage.Committed) or nameof(InterserverJournalStage.Completed);

    public static bool ReservationExpired(InterserverTransferRecord record, long unixTime) =>
        record.ExpiresUnixTime > 0 && record.ExpiresUnixTime < unixTime &&
        record.Stage == InterserverJournalStage.Reserved;

    public static bool IsStaleOwnershipEpoch(long incomingEpoch, IEnumerable<long> existingEpochs) =>
        existingEpochs.Any(x => x > incomingEpoch);

    public static InterserverTransferRecord? FindCurrentOutgoingRoute(
        string passengerUserId,
        IEnumerable<InterserverTransferRecord> outbox,
        IEnumerable<InterserverTransferRecord> inbox)
    {
        var outgoing = outbox
            .Where(x => (x.Stage is InterserverJournalStage.Committed or InterserverJournalStage.Completed) &&
                x.PassengerUserIds.Contains(passengerUserId, StringComparer.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.DestinationAddress))
            .OrderByDescending(x => x.UpdatedUnixTime)
            .FirstOrDefault();
        var incoming = inbox
            .Where(x => (x.Stage is InterserverJournalStage.Committed or InterserverJournalStage.Completed) &&
                x.PassengerUserIds.Contains(passengerUserId, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(x => x.UpdatedUnixTime)
            .FirstOrDefault();

        return outgoing == null || incoming != null && incoming.UpdatedUnixTime >= outgoing.UpdatedUnixTime
            ? null
            : outgoing;
    }

    public static bool CanTransition(InterserverJournalStage from, InterserverJournalStage to)
    {
        if (from == to)
            return true; // retries are idempotent
        if (to == InterserverJournalStage.Aborted)
            return from is InterserverJournalStage.Reserved or InterserverJournalStage.Charging or
                InterserverJournalStage.Uploaded;
        if (to == InterserverJournalStage.Failed)
            return from is not (InterserverJournalStage.Committed or InterserverJournalStage.Completed);
        return (from, to) switch
        {
            (InterserverJournalStage.Reserved, InterserverJournalStage.Charging) => true,
            (InterserverJournalStage.Reserved, InterserverJournalStage.Uploaded) => true,
            (InterserverJournalStage.Charging, InterserverJournalStage.Uploaded) => true,
            (InterserverJournalStage.Uploaded, InterserverJournalStage.Committing) => true,
            (InterserverJournalStage.Committing, InterserverJournalStage.Committed) => true,
            (InterserverJournalStage.Committed, InterserverJournalStage.Completed) => true,
            _ => false,
        };
    }
}
