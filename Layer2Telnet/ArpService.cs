﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using PcapDotNet.Base;
using PcapDotNet.Packets;
using PcapDotNet.Packets.IpV4;
using PcapDotNet.Packets.Ethernet;
using PcapDotNet.Packets.Arp;
using System.Threading;

namespace Layer2Net
{
    public class ArpService
    {
        private const int ARP_RESOLVE_TIMEOUT = 1000;
        private VirtualAdapter _adapter;
        private Dictionary<IpV4Address, MacAddress> _arp_table;
        private IpV4Address _current_arp_probe_target_ip;
        private MacAddress _current_arp_replay_target_mac;
        private ManualResetEvent _arp_resolve_wait_handle = new ManualResetEvent(false);

        public ArpService(VirtualAdapter Adapter)
        {
            _adapter = Adapter;
            _arp_table = new Dictionary<IpV4Address, MacAddress>();
        }

        public void Add(string IP, string Mac)
        {
            Add(new IpV4Address(IP), new MacAddress(Mac));
        }

        public void Add(IpV4Address IP, MacAddress Mac)
        {
            if (_arp_table.ContainsKey(IP))
            {
                _arp_table[IP] = Mac;
                VirtualNetwork.Instance.PostTraceMessage("ARP table item update: " + IP.ToString() + " = " + Mac.ToString());
            }
            else
            {
                _arp_table.Add(IP, Mac);
                VirtualNetwork.Instance.PostTraceMessage("ARP table item add: " + IP.ToString() + " = " + Mac.ToString());
            }
        }

        public void Remove(string IP)
        {
            if (IP != null && !string.IsNullOrEmpty(IP))
            {
                Remove(new IpV4Address(IP));
            }
            else
            {
                Remove(IpV4Address.Zero);
            }
        }

        public void Remove(IpV4Address IP)
        {
            if (IP != null && !IP.Equals(IpV4Address.Zero))
            {
                if (_arp_table.ContainsKey(IP))
                {
                    _arp_table.Remove(IP);
                    VirtualNetwork.Instance.PostTraceMessage("ARP table item remove: " + IP.ToString());
                }
            }
            else
            {
                _arp_table.Clear();
                VirtualNetwork.Instance.PostTraceMessage("ARP table clear");
            }
        }

        public void SendGratuitus()
        {
            EthernetLayer ethernetLayer = new EthernetLayer
            {
                Destination = UtilityLib.BroadcastMac,
                Source = _adapter.MAC,
                EtherType = EthernetType.None
            };

            VLanTaggedFrameLayer vlanLayer =
                new VLanTaggedFrameLayer
                {
                    PriorityCodePoint = ClassOfService.BestEffort,
                    CanonicalFormatIndicator = false,
                    VLanIdentifier = _adapter.VLAN,
                    EtherType = EthernetType.None,
                };

            ArpLayer arpLayer = new ArpLayer
            {
                SenderHardwareAddress = _adapter.MAC.ToBytes().AsReadOnly(),
                SenderProtocolAddress = _adapter.IP.ToBytes().AsReadOnly(),
                TargetHardwareAddress = MacAddress.Zero.ToBytes().AsReadOnly(),
                TargetProtocolAddress = _adapter.IP.ToBytes().AsReadOnly(),
                ProtocolType = EthernetType.IpV4,
                Operation = ArpOperation.Request,
            };

            if (_adapter.VLAN > 1)
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, vlanLayer, arpLayer));
            }
            else
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, arpLayer));
            }
            VirtualNetwork.Instance.PostTraceMessage("ARP Gratuitus: " + _adapter.IP.ToString() + " " + _adapter.MAC.ToString());
        }

        public void SendProbe(string TargetIP)
        {
            SendProbe(new IpV4Address(TargetIP));
        }

        public void SendProbe(IpV4Address TargetIP)
        {
            EthernetLayer ethernetLayer = new EthernetLayer
            {
                Destination = UtilityLib.BroadcastMac,
                Source = _adapter.MAC,
                EtherType = EthernetType.None
            };

            VLanTaggedFrameLayer vlanLayer =
                new VLanTaggedFrameLayer
                {
                    PriorityCodePoint = ClassOfService.BestEffort,
                    CanonicalFormatIndicator = false,
                    VLanIdentifier = _adapter.VLAN,
                    EtherType = EthernetType.None,
                };

            ArpLayer arpLayer = new ArpLayer
            {
                SenderHardwareAddress = _adapter.MAC.ToBytes().AsReadOnly(),
                SenderProtocolAddress = _adapter.IP.ToBytes().AsReadOnly(),
                TargetHardwareAddress = new MacAddress("FF:FF:FF:FF:FF:FF").ToBytes().AsReadOnly(),
                TargetProtocolAddress = TargetIP.ToBytes().AsReadOnly(),
                ProtocolType = EthernetType.IpV4,
                Operation = ArpOperation.Request,
            };

            if (_adapter.VLAN > 1)
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, vlanLayer, arpLayer));
            }
            else
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, arpLayer));
            }
            VirtualNetwork.Instance.PostTraceMessage("ARP Probe: " + TargetIP.ToString());
        }

        public void SendReply(IpV4Address DesIP, MacAddress DesMac)
        {
            EthernetLayer ethernetLayer = new EthernetLayer
            {
                Destination = UtilityLib.BroadcastMac,
                Source = _adapter.MAC,
                EtherType = EthernetType.None
            };

            VLanTaggedFrameLayer vlanLayer =
                new VLanTaggedFrameLayer
                {
                    PriorityCodePoint = ClassOfService.BestEffort,
                    CanonicalFormatIndicator = false,
                    VLanIdentifier = _adapter.VLAN,
                    EtherType = EthernetType.None,
                };

            ArpLayer arpLayer = new ArpLayer
            {
                SenderHardwareAddress = _adapter.MAC.ToBytes().AsReadOnly(),
                SenderProtocolAddress = _adapter.IP.ToBytes().AsReadOnly(),
                TargetHardwareAddress = DesMac.ToBytes().AsReadOnly(),
                TargetProtocolAddress = DesIP.ToBytes().AsReadOnly(),
                ProtocolType = EthernetType.IpV4,
                Operation = ArpOperation.Reply,
            };

            if (_adapter.VLAN > 1)
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, vlanLayer, arpLayer));
            }
            else
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, arpLayer));
            }

            VirtualNetwork.Instance.PostTraceMessage("ARP Reply: " + DesMac.ToString() + " - " + DesIP.ToString());
        }

        public bool Resolve(string TargetIP, out string ResolvedMac)
        {
            bool ResolveResult = false;
            IpV4Address TargetIpAddress = new IpV4Address(TargetIP);
            MacAddress ResolvedMacAddress = MacAddress.Zero;

            if (_arp_table.ContainsKey(TargetIpAddress))
            {
                ResolvedMac = _arp_table[TargetIpAddress].ToString();
                ResolveResult = true;
            }
            else
            {
                ResolveResult = Resolve(TargetIpAddress, out ResolvedMacAddress);
                if (ResolveResult)
                {
                    ResolvedMac = ResolvedMacAddress.ToString();
                }
                else
                {
                    ResolvedMac = string.Empty;
                }
            }

            return ResolveResult;
        }

        public bool Resolve(IpV4Address TargetIP, out MacAddress ResolvedMac)
        {
            bool ResolveResult = false;
            ResolvedMac = MacAddress.Zero;
            _arp_resolve_wait_handle.Reset();
            _current_arp_probe_target_ip = TargetIP;
            SendProbe(TargetIP);

            if (_arp_resolve_wait_handle.WaitOne(ARP_RESOLVE_TIMEOUT))
            {
                ResolveResult = true;
                ResolvedMac = _current_arp_replay_target_mac;
                VirtualNetwork.Instance.PostTraceMessage("ARP Resolve: " + TargetIP.ToString() + " - SUCCESSFUL: " + ResolvedMac.ToString());
            }
            else
            {
                VirtualNetwork.Instance.PostTraceMessage("ARP Resolve: " + TargetIP.ToString() + " - FAILED");
            }

            return ResolveResult;
        }

        public void ProcessARP(ArpDatagram packet)
        {
            VirtualNetwork.Instance.PostTraceMessage("ARP " + packet.Operation.ToString() + ": " + packet.SenderProtocolIpV4Address.ToString() + " looking for " + packet.TargetProtocolIpV4Address.ToString());
            if (packet.ProtocolType == EthernetType.IpV4)
            {
                if (_arp_table.ContainsKey(packet.SenderProtocolIpV4Address))
                {
                    _arp_table[packet.SenderProtocolIpV4Address] = packet.SenderHardwareAddress.ToArray().ToMacAddress();
                    VirtualNetwork.Instance.PostTraceMessage("ARP table item update: " + packet.SenderProtocolIpV4Address.ToString() + " = " + packet.SenderHardwareAddress.ToArray().ToMacAddress().ToString());
                }
                else
                {
                    _arp_table.Add(packet.SenderProtocolIpV4Address, packet.SenderHardwareAddress.ToArray().ToMacAddress());
                    VirtualNetwork.Instance.PostTraceMessage("ARP table item update: " + packet.SenderProtocolIpV4Address.ToString() + " = " + packet.SenderHardwareAddress.ToArray().ToMacAddress().ToString());
                }

                if (packet.Operation == ArpOperation.Reply && packet.SenderProtocolIpV4Address.Equals(_current_arp_probe_target_ip) && packet.TargetProtocolIpV4Address.Equals(_adapter.IP))
                {
                    _current_arp_replay_target_mac = packet.SenderHardwareAddress.ToArray().ToMacAddress();
                    _arp_resolve_wait_handle.Set();
                }

                if (packet.Operation == ArpOperation.Request && packet.TargetProtocolIpV4Address.Equals(_adapter.IP))
                {
                    SendReply(packet.SenderProtocolIpV4Address, packet.SenderHardwareAddress.ToArray().ToMacAddress());
                }
            }
        }
    }
}
