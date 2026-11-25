namespace ApcoaFlowApiClient.Models.Tariff;

/// <summary>
/// Response of <c>GET /authentication/v1/tariff?parkingLotId={id}</c>: the full pricing grid for a
/// lot's tariff(s). This is the authoritative rate structure the price ladder is derived from —
/// NOT the thin name/description shape an earlier model assumed.
///
/// UNIT GOTCHA: every <see cref="TariffGrid.AmountInCents"/> / <see cref="TariffMaxima.AmountInCents"/>
/// in this response is in 1/100 of a cent, i.e. divide by 10000 (not 100) to get the currency amount.
/// Verified against Carrefour TG lot 10584 (FLAT 20000/60 min = 2.00 PLN; INCREMENTAL 40000/60 min =
/// +4.00 PLN/h; DAY maxima 500000 = 50 PLN/day) matching live tariffBreakDownV2 quotes.
/// </summary>
public class TariffTemplatesResponse
{
    public List<TariffTemplate> TariffTemplates { get; set; } = [];
}

public class TariffTemplate
{
    public int TariffId { get; set; }
    public int ParkingLotId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>1 when this template is the active/default one for the lot.</summary>
    public int IsDefault { get; set; }
    public int IsActive { get; set; }

    /// <summary>Whether an early checkout is prorated/refunded for this tariff (0 = no proration).</summary>
    public int IsProrationAllowed { get; set; }

    /// <summary>Free minutes baked into the tariff itself (separate from promotions).</summary>
    public int FreeDurationInMinutes { get; set; }

    public List<TariffStack> Stacks { get; set; } = [];
}

public class TariffStack
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<TariffSlot> Slots { get; set; } = [];

    /// <summary>Caps (e.g. a per-DAY maximum) that bound the cumulative price.</summary>
    public List<TariffMaxima> Maxima { get; set; } = [];
}

/// <summary>
/// A time window (by day-of-week + time-of-day) whose <see cref="Grids"/> define how the price
/// accrues over the session's duration.
/// </summary>
public class TariffSlot
{
    public int StartDay { get; set; }
    public string? StartTime { get; set; }
    public int EndDay { get; set; }
    public string? EndTime { get; set; }

    /// <summary>When true the slot itself is free.</summary>
    public bool FreeSlot { get; set; }

    /// <summary>Upper bound of the slot in minutes (e.g. 1440 = one day).</summary>
    public int? MaxMinutes { get; set; }
    public int? MinMinutes { get; set; }

    public List<TariffGrid> Grids { get; set; } = [];
}

/// <summary>
/// One pricing step within a slot. <see cref="Type"/> is "FLAT" (a fixed charge for the first
/// <see cref="Duration"/> minutes) or "INCREMENTAL" (an added charge per subsequent block of
/// <see cref="Duration"/> minutes). <see cref="AmountInCents"/> is in 1/100-cent (÷10000 for PLN).
/// </summary>
public class TariffGrid
{
    public string? Type { get; set; }

    /// <summary>1/100 of a cent. Divide by 10000 for the currency amount (see class remarks).</summary>
    public decimal AmountInCents { get; set; }

    /// <summary>Block length in minutes this grid charge applies to.</summary>
    public int Duration { get; set; }

    public int Sequence { get; set; }
}

/// <summary>A maximum charge cap (e.g. a daily cap). <see cref="AmountInCents"/> is 1/100-cent.</summary>
public class TariffMaxima
{
    /// <summary>e.g. "DAY".</summary>
    public string? Type { get; set; }

    public int TypeSequence { get; set; }

    /// <summary>1/100 of a cent. Divide by 10000 for the currency amount.</summary>
    public decimal AmountInCents { get; set; }

    public int IsDurationBoss { get; set; }
}
