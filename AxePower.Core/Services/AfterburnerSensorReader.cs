using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace AxePower.Core.Services;

/// <summary>
/// Reads MSI Afterburner's live sensor table out of its <c>MAHMSharedMemory</c> block.
///
/// Afterburner gets CPU temperature the same way anything must — a kernel driver reading the
/// processor's thermal MSRs — but it already has that driver loaded and republishes every
/// reading in a shared memory mapping for other tools (RTSS, HWiNFO overlays) to consume. When
/// Afterburner is running this is the cheapest correct source AxePower has: no driver of our
/// own, no elevation needed, and the numbers match what the user already sees in Afterburner.
///
/// The mapping simply does not exist when Afterburner is closed, so every call is allowed to
/// fail and the caller falls through to the next source.
/// </summary>
public static class AfterburnerSensorReader
{
    private const string SharedMemoryName = "MAHMSharedMemory";

    /// <summary>'MAHM' little-endian — present once Afterburner has published a full table.</summary>
    private const uint Signature = 0x4D48414D;

    // Entry layout: five MAX_PATH ANSI strings, then the float payload.
    private const int MaxPath = 260;
    private const int SrcNameOffset = 0;
    private const int DataOffset = MaxPath * 5;

    /// <summary>A defensive ceiling so a corrupt header cannot drive a huge allocation.</summary>
    private const int MaxReasonableEntries = 512;

    /// <summary>
    /// Every published sensor, keyed by Afterburner's source name ("CPU temperature",
    /// "GPU temperature", "GPU usage", …). Empty when Afterburner is not running.
    /// </summary>
    public static Dictionary<string, float> TryReadSensors()
    {
        var sensors = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(0) != Signature) return sensors;

            int headerSize = (int)view.ReadUInt32(8);
            int entryCount = (int)view.ReadUInt32(12);
            int entrySize = (int)view.ReadUInt32(16);

            if (headerSize <= 0 || entrySize <= DataOffset + sizeof(float)) return sensors;
            if (entryCount <= 0 || entryCount > MaxReasonableEntries) return sensors;

            var nameBuffer = new byte[MaxPath];

            for (int i = 0; i < entryCount; i++)
            {
                long entryBase = headerSize + ((long)i * entrySize);

                view.ReadArray(entryBase + SrcNameOffset, nameBuffer, 0, MaxPath);
                string name = ReadAnsi(nameBuffer);
                if (string.IsNullOrWhiteSpace(name)) continue;

                float value = view.ReadSingle(entryBase + DataOffset);
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;

                // Afterburner publishes a row for every sensor it knows about, including ones
                // this machine has no hardware for; those sit at exactly zero.
                sensors[name] = value;
            }
        }
        catch (FileNotFoundException)
        {
            // Afterburner is not running — expected, and not worth logging on a timer.
        }
        catch
        {
            // Any other failure (permissions, a torn read mid-update) is treated the same way:
            // this source produced nothing on this tick.
        }

        return sensors;
    }

    /// <summary>True when Afterburner is running and publishing a readable sensor table.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return view.ReadUInt32(0) == Signature;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the first sensor whose name matches one of <paramref name="candidates"/> and whose
    /// value is inside <paramref name="min"/>..<paramref name="max"/>, or NaN. The range check
    /// filters out the placeholder zeros Afterburner emits for absent hardware.
    /// </summary>
    public static double Find(Dictionary<string, float> sensors, double min, double max, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (sensors.TryGetValue(candidate, out float value) && value >= min && value <= max)
                return value;
        }

        return double.NaN;
    }

    private static string ReadAnsi(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = buffer.Length;
        return Encoding.ASCII.GetString(buffer, 0, length).Trim();
    }
}
