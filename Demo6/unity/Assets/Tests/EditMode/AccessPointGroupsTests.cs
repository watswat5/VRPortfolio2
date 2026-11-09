using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace WifiHeatmap.Core.Tests
{
    /// <summary>The menu's networks: one SSID's radios on one channel together, single radios as they were.</summary>
    public sealed class AccessPointGroupsTests
    {
        private static List<List<(string bssid, string ssid, int channel, int rssi)>> Group(
            params (string bssid, string ssid, int channel, int rssi)[] radios) =>
            AccessPointGroups.Group(radios, r => r.ssid, r => r.channel, r => r.rssi);

        [Test]
        public void OneRadioPerNetworkStaysAsItWas()
        {
            var groups = Group(("A", "home", 6, -40), ("B", "neighbour", 1, -70), ("C", "home-5G", 36, -50));
            Assert.AreEqual(3, groups.Count);
            Assert.IsTrue(groups.All(g => g.Count == 1));
            CollectionAssert.AreEqual(new[] { "A", "C", "B" }, groups.Select(g => g[0].bssid), "strongest first");
        }

        [Test]
        public void OneSsidOnOneChannelIsOneEntryStrongestFirst()
        {
            var groups = Group(("A", "eduroam", 6, -80), ("B", "eduroam", 6, -55), ("C", "eduroam", 6, -70), ("D", "guest", 6, -60));
            Assert.AreEqual(2, groups.Count);
            CollectionAssert.AreEqual(new[] { "B", "C", "A" }, groups[0].Select(r => r.bssid));
            Assert.AreEqual("D", groups[1][0].bssid);
        }

        [Test]
        public void ChannelsStayApart()
        {
            var groups = Group(("A", "eduroam", 1, -60), ("B", "eduroam", 6, -62), ("C", "eduroam", 1, -75), ("D", "eduroam", 36, -50));
            Assert.AreEqual(3, groups.Count);
            CollectionAssert.AreEqual(new[] { 36, 1, 6 }, groups.Select(g => g[0].channel));
            Assert.AreEqual(2, groups[1].Count);
        }

        [Test]
        public void HiddenNetworksAreNeverPooled()
        {
            var groups = Group(("A", "", 6, -60), ("B", "", 6, -61), ("C", null, 6, -62));
            Assert.AreEqual(3, groups.Count);
        }

        [Test]
        public void SelectCommandAndRadioCount()
        {
            Assert.AreEqual("SELECT 4 AA:BB:CC:DD:EE:FF 6", ProbeProtocol.SelectCommand(4, "AA:BB:CC:DD:EE:FF", 6, false));
            Assert.AreEqual("SELECT 4 AA:BB:CC:DD:EE:FF 6 group=ssid", ProbeProtocol.SelectCommand(4, "AA:BB:CC:DD:EE:FF", 6, true));
            Assert.AreEqual(12, ProbeProtocol.MeasuredRadioCount("SELECTED 4 bssid=AA:BB:CC:DD:EE:FF channel=6 band=2.4 aps=12"));
            Assert.AreEqual(1, ProbeProtocol.MeasuredRadioCount("SELECTED 4 bssid=AA:BB:CC:DD:EE:FF channel=6 band=2.4"), "1.1 firmware");
        }
    }
}
