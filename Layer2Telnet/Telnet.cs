using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using PcapDotNet.Packets.IpV4;
using PcapDotNet.Packets.Ethernet;
using PcapDotNet.Packets.Transport;
using PcapDotNet.Packets;
using Communication.Interface;
using Communication.Interface.Implementation;
using Layer2Net;


namespace Layer2Telnet
{
    [InterfaceImplementation(Name = "L2Telnet", Scheme = "L2Telnet", ConfigPanel = null)]
    public class Telnet : AbsCommunicationInterface, ITcpSession
    {
        private enum TCP_STATE
        {
            CLOSED,
            LISTEN,
            SYN_SENT,
            SYN_RCVD,
            ESTABLISHED,
            FIN_WAIT_1,
            CLOSE_WAIT,
            FIN_WAIT_2,
            LAST_ACK,
            CLOSING,
            TIME_WAIT
        }

        private const ushort CONNECTION_TIMEOUT = 20000;
        private const ushort DISCONNECT_TIMEOUT = 2000;
        private const ushort TCP_OPEN_TIMEOUT = 5000;
        private const ushort KEEP_ALIVE_PERIOD = 500;
        private const ushort WRITE_RETRY_INTERVAL = 500;
        private const byte TTL = 128;
        private const ushort MAX_SEGMENT_SIZE = 1460;
        private const byte WINDOW_SCALE = 8;

        private TcpService _service = null;
        private VirtualAdapter _adapter = null;
        private ushort _local_port;
        private IpV4Address _remote_ip;
        private MacAddress _remote_mac;
        private ushort _remote_port;
        private bool _response_telnet_ctrl = true;
        private bool _send_gratuitus_when_no_response = true;

        private TCP_STATE _current_state = TCP_STATE.CLOSED;
        private ushort _current_ip_id = 30000;
        private uint _current_sequence_number = 0;
        private uint _next_ack_num = 0;
        private uint _last_acknowledgment_number = 0;
        private ushort _local_tcp_window_size = 65535;
        private ushort _remote_tcp_window_size = 0;
        private DateTime _last_read_available_time = DateTime.Now;
        private ManualResetEvent _connection_wait_handle = new ManualResetEvent(false);
        private bool _ack_status = true;
        private object _ack_status_lock = new object();

        private Queue<byte> InputBuffer;
        private object InputBufferLocker = new Object();
        private VirtualAdapter Adapter = null;

        public Telnet(string ConfigString, string FriendlyName) : base(ConfigString, FriendlyName)
        {
            // L2Telnet:IP=192.168.1.1, Port=23, Adapter=SOCKET_1, ConfigFile=Config\\TEST_NET.network
            if (VirtualNetwork.Instance == null)
            {
                string NetworkConfigFile = Config["ConfigFile"];
                VirtualNetwork.Load(NetworkConfigFile);
                VirtualNetwork.Instance.Start();
            }
            else
            {
                if (!VirtualNetwork.Instance.IsRunning)
                {
                    VirtualNetwork.Instance.Start();
                }
            }

            Adapter = VirtualNetwork.Instance.GetAdapterByName(Config["Adapter"]);
            Adapter.BoardcastLocalAddress();
            this._service = Adapter.TcpService;
            this._adapter = Adapter;
            this._local_port = this._service.GetAvailableLocalPort();
            this._remote_ip = new IpV4Address(Config["IP"]);
            this._remote_port = ushort.Parse(Config["Port"]);

            if (Config.ContainsKey("MAC") && !string.IsNullOrEmpty(Config["MAC"]))
            {
                this._remote_mac = new MacAddress(Config["MAC"]);
            }

            if (Config.ContainsKey("RESPONSE_TELNET_CTRL") && !string.IsNullOrEmpty(Config["RESPONSE_TELNET_CTRL"]))
            {
                this._response_telnet_ctrl = bool.Parse(Config["RESPONSE_TELNET_CTRL"]);
            }

            if (Config.ContainsKey("SEND_GRATUITUS") && !string.IsNullOrEmpty(Config["SEND_GRATUITUS"]))
            {
                this._send_gratuitus_when_no_response = bool.Parse(Config["SEND_GRATUITUS"]);
            }

            _current_state = TCP_STATE.CLOSED;
            this._service.AddSession(this);
        }

        public string Name
        {
            get
            {
                return _remote_ip.ToString() + ":" + _remote_port.ToString();
            }
        }

        public ushort LocalPort
        {
            get
            {
                return _local_port;
            }
        }

        public bool IsOpen
        {
            get
            {
                return _current_state == TCP_STATE.ESTABLISHED;
            }
        }

        public uint HashCode
        {
            get
            {
                return UtilityLib.GetTcpSessionHashCode(_adapter.IP, _local_port, _remote_ip, _remote_port);
            }
        }

        override public bool IsOpened
        {
            get
            {
                try
                {
                    return IsOpen;
                }
                catch
                {
                    return false;
                }
            }
        }

        override public void Open()
        {
            string MACString = string.Empty;
            _adapter.BoardcastLocalAddress();
            if (_adapter.ArpService.Resolve(_remote_ip.ToString(), out MACString))
            {
                this._remote_mac = new MacAddress(MACString);
            }
            else
            {
                throw new Exception("Unable resolve mac for remote host: " + _remote_ip.ToString());
            }
            DateTime StartTime = DateTime.Now;
            TimeSpan ConnectionTime = TimeSpan.Zero;
            while (!IsOpen && ConnectionTime.TotalMilliseconds < CONNECTION_TIMEOUT)
            {
                TcpOpen();
                ConnectionTime = DateTime.Now - StartTime;
            } 
        }

        private void TcpOpen()
        {
            VirtualNetwork.Instance.PostTraceMessage("TCP OPEN: " + _remote_ip.ToString() + " " + _remote_port.ToString());
            InputBuffer = new Queue<byte>();
            _connection_wait_handle.Reset();

            TcpOptions tcpOptions = new TcpOptions(
                new TcpOptionMaximumSegmentSize(MAX_SEGMENT_SIZE),
                new TcpOptionWindowScale(WINDOW_SCALE)
                );

            SendTcpCtrlPacket(0, TcpControlBits.Synchronize, tcpOptions);    // ACK = false, SYNC = true, FIN = false
            _current_state = TCP_STATE.SYN_SENT;
            _connection_wait_handle.WaitOne(TCP_OPEN_TIMEOUT, true); // wait for connection process finish
            if (_current_state == TCP_STATE.ESTABLISHED)
            {
                VirtualNetwork.Instance.PostTraceMessage("TCP OPEN: " + _remote_ip.ToString() + " " + _remote_port.ToString() + " - SUCCESSFUL");
            }
            else
            {
                VirtualNetwork.Instance.PostTraceMessage("TCP OPEN: " + _remote_ip.ToString() + " " + _remote_port.ToString() + " - FAILED");
            }
        }

        override public void Close()
        {
            if (IsOpen)
            {
                VirtualNetwork.Instance.PostTraceMessage("TCP CLOSE: " + _remote_ip.ToString() + " " + _remote_port.ToString());
                _connection_wait_handle.Reset();
                SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Fin | TcpControlBits.Acknowledgment);
                _current_state = TCP_STATE.FIN_WAIT_1;
                _connection_wait_handle.WaitOne(DISCONNECT_TIMEOUT, true); // wait for connection process finish

                if (_current_state == TCP_STATE.CLOSED)
                {
                    VirtualNetwork.Instance.PostTraceMessage("TCP CLOSE: " + _remote_ip.ToString() + " " + _remote_port.ToString() + " - SUCCESSFUL");
                }
                else
                {
                    VirtualNetwork.Instance.PostTraceMessage("TCP CLOSE: " + _remote_ip.ToString() + " " + _remote_port.ToString() + " - FAILED");
                }
            }
        }

        override public void Flush()
        {
            return;
        }

        override public int ReadByte()
        {
            int data = -1;

            if (InputBuffer.Count > 0)
            {
                lock (InputBufferLocker)
                {
                    if (InputBuffer.Count > 0)
                    {
                        data = InputBuffer.Dequeue();
                    }
                }
                _last_read_available_time = DateTime.Now;
            }
            else
            {
                if (_send_gratuitus_when_no_response && IsOpen)
                {
                    if ((DateTime.Now - _last_read_available_time).TotalMilliseconds >= KEEP_ALIVE_PERIOD)
                    {
                        _adapter.ArpService.SendGratuitus();
                        _last_read_available_time = DateTime.Now;
                    }
                }
            }

            return data;
        }

        override public void Write(byte data)
        {
            if (IsOpen)
            {
                SendPacket(new byte[] { data });
            }
        }

        override public void Write(byte[] data)
        {
            if (IsOpen)
            {
                SendPacket(data);
            }
        }

        public void ProcessTCP(IpV4Datagram packet)
        {
            IpV4Datagram ip = packet;
            TcpDatagram tcp = packet.Tcp;

            if (ip.Source.Equals(_remote_ip) && tcp.SourcePort.Equals(_remote_port) && ip.Destination.Equals(_adapter.IP) && tcp.DestinationPort.Equals(_local_port))
            {
                bool SYN = tcp.IsSynchronize;
                bool ACK = tcp.IsAcknowledgment;
                bool FIN = tcp.IsFin;
                bool PSH = tcp.IsPush;
                bool RST = tcp.IsReset;
                _remote_tcp_window_size = tcp.Window;

                if (_current_state == TCP_STATE.CLOSED)
                {
                    SendTcpCtrlPacket(tcp.SequenceNumber + 1, TcpControlBits.Reset | TcpControlBits.Acknowledgment);
                }
                else if (_current_state == TCP_STATE.SYN_SENT && SYN && ACK)  // 远程主机响应连接请求
                {
                    SendTcpCtrlPacket(tcp.SequenceNumber + 1, TcpControlBits.Acknowledgment);
                    _current_state = TCP_STATE.ESTABLISHED;
                    _connection_wait_handle.Set();
                    _service.TriggerSessionStateChange(this);
                }
                else if (FIN) // 连接被将要断开
                {
                    if (_current_state == TCP_STATE.ESTABLISHED)
                    {
                        _current_state = TCP_STATE.CLOSE_WAIT;
                    }
                    else if (_current_state == TCP_STATE.FIN_WAIT_1)
                    {
                        _current_state = ACK ? TCP_STATE.TIME_WAIT : TCP_STATE.CLOSING;
                    }
                    else if (_current_state == TCP_STATE.FIN_WAIT_2)
                    {
                        _current_state = TCP_STATE.TIME_WAIT;
                    }

                    SendTcpCtrlPacket(tcp.SequenceNumber + 1, TcpControlBits.Acknowledgment);
                }
                else if (_current_state == TCP_STATE.FIN_WAIT_1 && ACK)
                {
                    _current_state = TCP_STATE.FIN_WAIT_2;
                }
                else if (_current_state == TCP_STATE.CLOSING && ACK)
                {
                    _current_state = TCP_STATE.TIME_WAIT;
                }
                else if (_current_state == TCP_STATE.LAST_ACK && ACK)
                {
                    // SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Reset);
                    _current_state = TCP_STATE.CLOSED;
                }
                else if (_current_state == TCP_STATE.ESTABLISHED && RST) // 连接被重置
                {
                    _current_state = TCP_STATE.CLOSED;
                }
                else if (PSH || ((ACK) && tcp.PayloadLength > 0))   // 需处理传输数据
                {
                    if (ACK && tcp.SequenceNumber == _last_acknowledgment_number - 1)  // Keep Alive
                    {
                        SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Acknowledgment);
                    }
                    else if (ACK && tcp.AcknowledgmentNumber >= _next_ack_num)
                    {
                        _current_sequence_number = _next_ack_num;
                        lock (_ack_status_lock)
                        {
                            _ack_status = true;
                        }
                    }

                    // 只处理顺序正确的数据包
                    if (tcp.SequenceNumber == _last_acknowledgment_number)
                    {
                        try
                        {
                            MemoryStream PayloadStream = tcp.Payload.ToMemoryStream();
                            lock (InputBufferLocker)
                            {
                                for (int i = 0; i < PayloadStream.Length; i++)
                                {
                                    InputBuffer.Enqueue((byte)PayloadStream.ReadByte());
                                }
                            }

                            SendTcpCtrlPacket(tcp.SequenceNumber + (uint)tcp.PayloadLength, TcpControlBits.Acknowledgment);
                        } catch {}
                    }
                    else
                    {
                        // 对于顺序不正确的包，告诉远端下一个包的顺序应该是多少
                        SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Acknowledgment);
                    }

                }
                else if (ACK && _current_state == TCP_STATE.ESTABLISHED)
                {
                    if (tcp.SequenceNumber == _last_acknowledgment_number - 1) // Keep Alive
                    {
                        SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Acknowledgment);
                    }
                    else if (tcp.AcknowledgmentNumber >= _next_ack_num)
                    {
                        _current_sequence_number = _next_ack_num;
                        lock (_ack_status_lock)
                        {
                            _ack_status = true;
                        }
                    }
                }

                if (_current_state == TCP_STATE.CLOSE_WAIT)
                {
                    SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Fin | TcpControlBits.Acknowledgment);
                    _current_state = TCP_STATE.LAST_ACK;
                }

                if (_current_state == TCP_STATE.TIME_WAIT)
                {
                    Thread.Sleep(500);
                    // SendTcpCtrlPacket(_last_acknowledgment_number, TcpControlBits.Reset);
                    _current_state = TCP_STATE.CLOSED;
                    _connection_wait_handle.Set();
                }

                if (_current_state == TCP_STATE.CLOSED)
                {
                    _service.TriggerSessionStateChange(this);
                }
            }
        }

        void SendTcpCtrlPacket(uint AcknowledgmentNumber, TcpControlBits CtrlBits)
        {
            SendTcpCtrlPacket(AcknowledgmentNumber, CtrlBits, TcpOptions.None);
        }

        void SendTcpCtrlPacket(uint AcknowledgmentNumber, TcpControlBits CtrlBits, TcpOptions TcpOptions)
        {
            _last_acknowledgment_number = AcknowledgmentNumber;
            EthernetLayer ethernetLayer =
                new EthernetLayer
                {
                    Source = _adapter.MAC,
                    Destination = _remote_mac,
                    EtherType = EthernetType.None, // Will be filled automatically.
                };

            VLanTaggedFrameLayer vlanLayer =
                new VLanTaggedFrameLayer
                {
                    PriorityCodePoint = ClassOfService.Background,
                    CanonicalFormatIndicator = false,
                    VLanIdentifier = _adapter.VLAN,
                    EtherType = EthernetType.None,
                };

            IpV4Layer ipV4Layer =
                new IpV4Layer
                {
                    Source = _adapter.IP,
                    CurrentDestination = _remote_ip,
                    Fragmentation = new IpV4Fragmentation(IpV4FragmentationOptions.DoNotFragment, 0),
                    HeaderChecksum = null, // Will be filled automatically.
                    Identification = _current_ip_id++,
                    Options = IpV4Options.None,
                    Protocol = null, // Will be filled automatically.
                    Ttl = TTL,
                    TypeOfService = 0,
                };

            TcpLayer tcpLayer =
                new TcpLayer
                {
                    SourcePort = _local_port,
                    DestinationPort = _remote_port,
                    Checksum = null, // Will be filled automatically.
                    SequenceNumber = _current_sequence_number,
                    AcknowledgmentNumber = _last_acknowledgment_number,
                    ControlBits = CtrlBits,
                    Window = _local_tcp_window_size,
                    UrgentPointer = 0,
                    Options = TcpOptions
                };

            if (_adapter.VLAN > 1)
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, vlanLayer, ipV4Layer, tcpLayer));
            }
            else
            {
                VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, ipV4Layer, tcpLayer));
            }

            if (CtrlBits != TcpControlBits.Acknowledgment)
            {
                _current_sequence_number++;
                _next_ack_num = _current_sequence_number;
            }
        }

        public void SendPacketInternal(byte[] data)
        {
            Packet packet = null;
            EthernetLayer ethernetLayer =
                new EthernetLayer
                {
                    Source = _adapter.MAC,
                    Destination = _remote_mac,
                    EtherType = EthernetType.None, // Will be filled automatically.
                };

            VLanTaggedFrameLayer vlanLayer =
                new VLanTaggedFrameLayer
                {
                    PriorityCodePoint = ClassOfService.Background,
                    CanonicalFormatIndicator = false,
                    VLanIdentifier = _adapter.VLAN,
                    EtherType = EthernetType.None,
                };

            IpV4Layer ipV4Layer =
                new IpV4Layer
                {
                    Source = _adapter.IP,
                    CurrentDestination = _remote_ip,
                    Fragmentation = new IpV4Fragmentation(IpV4FragmentationOptions.DoNotFragment, 0),
                    HeaderChecksum = null, // Will be filled automatically.
                    Identification = _current_ip_id++,
                    Options = IpV4Options.None,
                    Protocol = null, // Will be filled automatically.
                    Ttl = TTL,
                    TypeOfService = 0,
                };

            TcpLayer tcpLayer =
                new TcpLayer
                {
                    SourcePort = _local_port,
                    DestinationPort = _remote_port,
                    Checksum = null, // Will be filled automatically.
                    SequenceNumber = _current_sequence_number,
                    AcknowledgmentNumber = _last_acknowledgment_number,
                    ControlBits = TcpControlBits.Push | TcpControlBits.Acknowledgment,
                    Window = _local_tcp_window_size,
                    UrgentPointer = 0,
                    Options = TcpOptions.None
                };

            PayloadLayer payloadLayer = new PayloadLayer();

            if (data.Length > _remote_tcp_window_size)
            {
                uint offset = 0;
                uint data_to_send = (uint)data.Length;
                while (data_to_send > 0)
                {
                    tcpLayer.SequenceNumber = _current_sequence_number;

                    if (data_to_send > _remote_tcp_window_size && _remote_tcp_window_size > 0)
                    {
                        byte[] send_buffer = new byte[_remote_tcp_window_size];
                        Array.Copy(data, offset, send_buffer, 0, _remote_tcp_window_size);
                        payloadLayer.Data = new Datagram(send_buffer);
                        offset += _remote_tcp_window_size;
                        _next_ack_num = _current_sequence_number + _remote_tcp_window_size;
                    }
                    else
                    {
                        byte[] send_buffer = new byte[data_to_send];
                        Array.Copy(data, offset, send_buffer, 0, data_to_send);
                        payloadLayer.Data = new Datagram(send_buffer);
                        offset += data_to_send;
                        _next_ack_num = _current_sequence_number + data_to_send;
                    }
                    data_to_send = (uint)data.Length - offset - 1;

                    if (_adapter.VLAN > 1)
                    {
                        packet = PacketBuilder.Build(DateTime.Now, ethernetLayer, vlanLayer, ipV4Layer, tcpLayer, payloadLayer);
                    }
                    else
                    {
                        VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, ipV4Layer, tcpLayer, payloadLayer));
                    }
                }
            }
            else
            {
                _next_ack_num = _current_sequence_number + (uint)data.Length;
                payloadLayer.Data = new Datagram(data);

                if (_adapter.VLAN > 1)
                {
                    VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, vlanLayer, ipV4Layer, tcpLayer, payloadLayer));
                }
                else
                {
                    VirtualNetwork.Instance.SendPacket(PacketBuilder.Build(DateTime.Now, ethernetLayer, ipV4Layer, tcpLayer, payloadLayer));               
                }
            }
        }

        public void SendPacket(byte[] data)
        {
            lock (_ack_status_lock)
            {
                _ack_status = false;
            }

            DateTime start_time = DateTime.Now;
            DateTime last_send_time = DateTime.Now;

            SendPacketInternal(data);

            while (!_ack_status && ((DateTime.Now - start_time).TotalMilliseconds < (WriteTimeout * 1000)))
            {
                Thread.Sleep(50);
                if ((DateTime.Now - last_send_time).TotalMilliseconds >= WRITE_RETRY_INTERVAL)
                {
                    last_send_time = DateTime.Now;
                    SendPacketInternal(data);
                }
            }

            if (!_ack_status)
            {
                if (_current_state == TCP_STATE.CLOSED)
                {
                    System.Console.WriteLine("Connection closed by remote host!");
                }
                else
                {
                    System.Console.WriteLine("No ack from remote host!");
                }
            }
        }

        override public string ReadUntil(string StopFlag)
        {
            read_buffer.Clear();
            int data = -1;
            int cmd = -1;
            int option = -1;
            int previous_data = -1;
            int previous_data2 = -1;

            do
            {
                previous_data2 = previous_data;
                previous_data = data;
                data = ReadByte();

                switch (data)
                {
                    case -1:
                        {
                            /*
                            if (!GoAhead)
                            {
                                Write(new byte[] { (byte)TELNET_CMD.IAC, (byte)TELNET_CMD.DO, (byte)TELNET_OPT.SGA });
                                GoAhead = true;
                            }
                            */
                        }
                        break;
                    case (int)TELNET_CMD.IAC:
                        {
                            cmd = ReadByte();
                            if (cmd == -1) break;

                            switch ((TELNET_CMD)cmd)
                            {
                                case TELNET_CMD.IAC:   // data 0xFF
                                        read_buffer.Append((byte)cmd);
                                    break;
                                case TELNET_CMD.DO:
                                    {
                                        option = ReadByte();
                                        if (_response_telnet_ctrl)
                                        {
                                            Write(new byte[] { (byte)TELNET_CMD.IAC, (byte)TELNET_CMD.WONT, (byte)option });
                                        }
                                        break;
                                    }
                                case TELNET_CMD.DONT:
                                    {
                                        option = ReadByte(); //  Yes, I hear you.
                                        break;
                                    }
                                case TELNET_CMD.WILL:
                                    {
                                        option = ReadByte();
                                        if (_response_telnet_ctrl)
                                        {
                                            Write(new byte[] { (byte)TELNET_CMD.IAC, (byte)TELNET_CMD.DO, (byte)option });
                                        }
                                        break;
                                    }
                                case TELNET_CMD.WONT:
                                    {
                                        option = ReadByte(); //  Yes, I hear you.
                                        break;
                                    }
                                default:
                                    break;
                            }
                            break;
                        }
                    case 0:
                        // Do Nothing if read 0
                        break;
                    default:
                        read_buffer.Append((byte)data);
                        break;
                }

                if (StopFlag != null && !StopFlag.Equals(string.Empty))
                {
                    if (((IBuffer)read_buffer).Contains(StopFlag))
                    {
                        break;
                    }
                }

            } while (data != -1);

            if (!read_buffer.IsEmpty())
            {
                global_buffer.Append((IBufferInternal)read_buffer);
                if (fragment_buffer_record)
                {
                    fragment_buffer.Append((IBufferInternal)read_buffer);
                }

                TriggerBufferUpdateEvent(read_buffer);
            }
            return read_buffer.ToString();
        }

        internal enum TELNET_CMD
        {
            XEOF = 236,         // End of file
            SUSP = 237,         // Suspend process
            ABORT = 238,        // Abort process
            EOR = 239,          // End of record
            SE = 240,           // End of subnegotiation parameters.
            NOP = 241,          // No operation.
            DATA_MARK = 242,    // The data stream portion of a Synch. This should always be accompanied by a TCP Urgent notification.
            BREAK = 243,        // NVT character BRK.
            IP = 244,           // Interrupt Process
            AO = 245,           // Abort output
            AYT = 246,          // Are You There
            EC = 247,           // Erase character
            EL = 248,           // Erase Line
            GA = 249,           // Go ahead
            SB = 250,           // Indicates that what follows is subnegotiation of the indicated option.
            WILL = 251,         // Indicates the desire to begin performing, or confirmation that you are now performing, the indicated option.
            WONT = 252,         // Indicates the refusal to perform, or continue performing, the indicated option.
            DO = 253,           // Indicates the request that the other party perform, or confirmation that you are expecting the other party to perform, the indicated option.
            DONT = 254,         // Indicates the demand that the other party stop performing, or confirmation that you are no longer expecting the other party to perform, the indicated option.
            IAC = 255           // Data Byte 255.
        }

        internal enum TELNET_OPT
        {
            BINARY = 0,             // 8-bit data path
            ECHO = 1,               // echo
            RCP = 2,                // prepare to reconnect
            SGA = 3,                // suppress go ahead
            NAMS = 4,               // approximate message size
            STATUS = 5,             // give status
            TM = 6,                 // timing mark
            RCTE = 7,               // remote controlled transmission and echo
            NAOL = 8,               // negotiate about output line width
            NAOP = 9,               // negotiate about output page size
            NAOCRD = 10,            // negotiate about CR disposition
            NAOHTS = 11,            // negotiate about horizontal tabstops
            NAOHTD = 12,            // negotiate about horizontal tab disposition
            NAOFFD = 13,            // negotiate about formfeed disposition
            NAOVTS = 14,            // negotiate about vertical tab stops
            NAOVTD = 15,            // negotiate about vertical tab disposition
            NAOLFD = 16,            // negotiate about output LF disposition
            XASCII = 17,            // extended ascic character set
            LOGOUT = 18,            // force logout
            BM = 19,                // byte macro
            DET = 20,               // data entry terminal
            SUPDUP = 21,            // supdup protocol
            SUPDUPOUTPUT = 22,      // supdup output
            SNDLOC = 23,            // send location
            TTYPE = 24,             // terminal type
            EOR = 25,               // end or record
            TUID = 26,              // TACACS user identification
            OUTMRK = 27,            // output marking
            TTYLOC = 28,            // terminal location number
            _3270REGIME = 29,       // 3270 regime
            X3PAD = 30,             // X.3 PAD
            NAWS = 31,              // window size
            TSPEED = 32,            // terminal speed
            LFLOW = 33,             // remote flow control
            LINEMODE = 34,          // Linemode option
            XDISPLOC = 35,          // X Display Location
            OLD_ENVIRON = 36,       // Old - Environment variables
            AUTHENTICATION = 37,    // Authenticate
            ENCRYPT = 38,           // Encryption option
            NEW_ENVIRON = 39,       // New - Environment variables
            TN3270E = 40,           // TN3270 enhancements
            XAUTH = 41,
            CHARSET = 42,           // Character set
            RSP = 43,               // Remote serial port
            COM_PORT_OPTION = 44,   // Com port control
            SLE = 45,               // Suppress local echo
            STARTTLS = 46,          // Start TLS
            KERMIT = 47,            // Automatic Kermit file transfer
            SEND_URL = 48,
            FORWARD_X = 49,
            PRAGMA_LOGON = 138,
            SSPI_LOGON = 139,
            PRAGMA_HEARTBEAT = 140,
            EXOPL = 255             // extended-options-list
        }

    }
}
