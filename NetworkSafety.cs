namespace TourneyMode;

/// <summary>A NIC experiment failed; RecoveryRequired means original state is not durably verified.</summary>
public sealed class NetworkTransitionException : InvalidOperationException
{
    public bool RecoveryRequired { get; }

    public NetworkTransitionException(string message, bool recoveryRequired, Exception innerException)
        : base(message, innerException) => RecoveryRequired = recoveryRequired;
}

/// <summary>
/// A reconnecting NIC experiment is a transaction. The facade's restore method
/// must return only after verifying the original values and usable link state.
/// Root activation decides whether a verified rollback permits other features.
/// </summary>
public static class NetworkSafety
{
    public static void Apply(ISystemFacade system, EthernetAdapterBackup adapter, Action persist, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(log);
        EthernetTuning.ValidateBackup(adapter);
        if (adapter.Properties.Count == 0) return;
        if (!adapter.ApplyAttempted || adapter.Restored)
            throw new InvalidOperationException("A fresh, journaled Ethernet apply attempt is required before a NIC experiment.");

        // Failure here precedes every mutation. Do not turn a journal-write
        // failure into a driver change or a needless reconnecting rollback.
        persist();
        try
        {
            system.ApplyEthernetAdapter(adapter);
            persist();
            SafeLog(log, "Ethernet experiment applied and verified. The policy is an experiment, not a universal latency improvement.");
        }
        catch (Exception applyError)
        {
            SafeLog(log, "Ethernet experiment failed; restoring the recorded driver settings before continuing.");
            Exception? rollbackError = null;
            try
            {
                // WindowsSystem delegates to EthernetTuning.Restore, which
                // verifies both the original values and link Up before return.
                system.RestoreEthernetAdapter(adapter);
                adapter.Restored = true;
                persist();
            }
            catch (Exception error)
            {
                rollbackError = error;
                adapter.Restored = false;
                // The pre-apply journal remains authoritative if this best
                // effort write also fails. Never discard its saved originals.
                try { persist(); }
                catch (Exception saveError) { rollbackError = new AggregateException(error, saveError); }
            }

            if (rollbackError != null)
            {
                SafeLog(log, "Ethernet recovery could not be verified. Further activation must stop; saved recovery remains necessary.");
                throw new NetworkTransitionException(
                    "Ethernet experiment failed and rollback is unverified. Stop activation and restore the recorded NIC settings outside play.",
                    true, new AggregateException(applyError, rollbackError));
            }

            SafeLog(log, "Ethernet experiment was rolled back and the recorded original settings were verified.");
            throw new NetworkTransitionException(
                "Ethernet experiment failed; original driver settings were restored and verified.", false, applyError);
        }
    }

    private static void SafeLog(Action<string> log, string message)
    {
        // A view/log callback must never prevent a safety rollback.
        try { log(message); } catch { }
    }
}
