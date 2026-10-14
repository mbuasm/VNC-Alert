namespace MaszynyAlert.Core.Alerts;

public enum ProgressState
{
    /// <summary>Postęp powyżej 0 % i poniżej 100 % (także „0.20%”) — maszyna frezuje bieżącą pracę.</summary>
    Running = 0,

    /// <summary>Postęp na 100 % (próg <c>IdleWatchProfile.FullAtPercent</c>/<c>FullAtFraction</c>) — praca skończona, maszyna czeka na kolejną.</summary>
    Full = 1,

    Zero = 2,
}
