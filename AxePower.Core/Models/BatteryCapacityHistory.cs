using System;
using System.Collections.Generic;

namespace AxePower.Core.Models;

/// <summary>One recorded full-charge capacity, at the end of a battery-report period.</summary>
public sealed record BatteryCapacityPoint(DateTime EndUtc, double FullChargeMwh);

/// <summary>
/// How the battery's full-charge capacity has changed, as Windows recorded it. Points are oldest
/// first. <see cref="DesignMwh"/> is 0 when the report did not state a design capacity.
/// </summary>
public sealed record BatteryCapacityHistory(IReadOnlyList<BatteryCapacityPoint> Points, double DesignMwh);
