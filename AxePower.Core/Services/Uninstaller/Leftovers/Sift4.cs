// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.
// Sift4 is by Siderite (https://siderite.dev/blog/super-fast-and-accurate-string-distance.html).

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary>Sift4 string distance ("simplest" variant): fast and close to Levenshtein for similar strings.</summary>
public static class Sift4
{
    /// <param name="maxOffset">How far ahead to look for a matching character after a mismatch.</param>
    public static int SimplestDistance(string s1, string s2, int maxOffset)
    {
        if (string.IsNullOrEmpty(s1)) return string.IsNullOrEmpty(s2) ? 0 : s2.Length;
        if (string.IsNullOrEmpty(s2)) return s1.Length;

        int l1 = s1.Length, l2 = s2.Length;
        int c1 = 0, c2 = 0, lcss = 0, localCs = 0;

        while (c1 < l1 && c2 < l2)
        {
            if (s1[c1] == s2[c2])
            {
                localCs++;
            }
            else
            {
                lcss += localCs;
                localCs = 0;
                if (c1 != c2) c1 = c2 = Math.Max(c1, c2);
                for (int i = 0; i < maxOffset && (c1 + i < l1 || c2 + i < l2); i++)
                {
                    if (c1 + i < l1 && s1[c1 + i] == s2[c2])
                    {
                        c1 += i;
                        localCs++;
                        break;
                    }
                    if (c2 + i < l2 && s1[c1] == s2[c2 + i])
                    {
                        c2 += i;
                        localCs++;
                        break;
                    }
                }
            }
            c1++;
            c2++;
        }

        lcss += localCs;
        return Math.Max(l1, l2) - lcss;
    }
}
