using System;
using AxePower.Core.Models;

namespace AxePower.Core.Helpers;

public static class TemperatureHelper
{
    public static double ToFahrenheit(double celsius) => (celsius * 9.0 / 5.0) + 32.0;

    public static double ToCelsius(double fahrenheit) => (fahrenheit - 32.0) * 5.0 / 9.0;

    public static string Format(double? celsius, TemperatureUnit unit, bool includeDecimals = false)
    {
        if (!celsius.HasValue) return "Not reported";

        if (unit == TemperatureUnit.Fahrenheit)
        {
            var f = ToFahrenheit(celsius.Value);
            return includeDecimals ? $"{f:0.0} °F" : $"{Math.Round(f)} °F";
        }

        return includeDecimals ? $"{celsius.Value:0.0} °C" : $"{Math.Round(celsius.Value)} °C";
    }
}
