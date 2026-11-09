using System;
using System.Collections.Generic;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// Groups a scan's radios into the networks the menu lists: the radios of one SSID on one channel are one entry
    /// (an institution's network has dozens of access points; measured together, every one of them counts towards the
    /// reading). A network with a single radio there stays a single radio, as do hidden networks (an empty SSID says
    /// nothing about which network a radio belongs to). Each group lists its radios strongest first; the groups are
    /// in order of their strongest radio.
    /// </summary>
    public static class AccessPointGroups
    {
        public static List<List<T>> Group<T>(IEnumerable<T> radios, Func<T, string> ssid, Func<T, int> channel, Func<T, int> rssi)
        {
            var groups = new List<List<T>>();
            var byNetwork = new Dictionary<(string, int), List<T>>();
            foreach (T radio in radios)
            {
                string name = ssid(radio);
                if (string.IsNullOrEmpty(name))
                {
                    groups.Add(new List<T> { radio });
                    continue;
                }
                var key = (name, channel(radio));
                if (!byNetwork.TryGetValue(key, out List<T> group))
                {
                    group = new List<T>();
                    byNetwork.Add(key, group);
                    groups.Add(group);
                }
                group.Add(radio);
            }
            foreach (List<T> group in groups)
            {
                StableSortByRssi(group, rssi);
            }
            StableSortByRssi(groups, g => rssi(g[0]));
            return groups;
        }

        // List.Sort isn't stable; equal signals keep scan order.
        private static void StableSortByRssi<T>(List<T> items, Func<T, int> rssi)
        {
            var indexed = new List<(T item, int index)>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                indexed.Add((items[i], i));
            }
            indexed.Sort((a, b) =>
            {
                int byRssi = rssi(b.item).CompareTo(rssi(a.item));
                return byRssi != 0 ? byRssi : a.index.CompareTo(b.index);
            });
            for (int i = 0; i < items.Count; i++)
            {
                items[i] = indexed[i].item;
            }
        }
    }
}
