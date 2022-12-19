using System;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Timers;
using System.Threading;

using gplat;

namespace gplat
{
	public enum sock_state_e
	{
		_BEGIN,
		not_connected,
		connecting,
		connected,
		disconnecting,
		disconnected,
		_END
	}

	public enum sock_send_state_e
	{
		_BEGIN,
		not_sending,
		sending,
		_END
	}

	public enum sock_recv_state_e
	{
		_BEGIN,
		not_receiving,
		receiving,
		_END
	}

	public enum dispose_state_e
	{
		_BEGIN,
		not_disposed,
		disposed,
		_END
	}


	public partial class TcpSocket
		: TcpSocketType, IDisposable
	{
		protected log4net.ILog m_log = gplat.Log.logger("tcp.socket");

		protected string m_hostname;
		protected UInt16 m_port;
		protected RawSocket m_raw_socket;
		protected bool m_timer_thread_active = false;

		private readonly object m_socket_mutex = new object(); // mutex
		public MessageNotifier m_message_notifier = new MessageNotifier(gplat_define.notifier_type_e.SOCKET, "notifier.tcp");
		private gplat.MessageReceiver m_socket_message_reciver = new gplat.MessageReceiver("notifier.socket"); // socket 관련 메시지 처리 
		protected bool m_socket_message_receiver_registered = false;

		protected PacketQueue m_send_packet_queue = new PacketQueue(); //보낼 패킷 큐, 재접속 후 재시도 함.
		protected gplat.PacketDictionary m_send_failed_packets = new gplat.PacketDictionary(); //전송 실패 패킷 관리용.

		// function option  
		public bool m_use_async_send = true;

		public UInt32 m_send_sequence = 0;

		public bool m_use_encryption = true;

		//오프라인 전송 중 
		public bool m_offline_sending = false;
		//public bool m_use_offlinesend = true;

		// acceptor / connector 용도 체크, acceptor에서 생성하는 소켓인 경우 true by joygram 2022/12/08
		public bool m_for_acceptor = false;


		public SyncState<sock_state_e> m_sock_state = new SyncState<sock_state_e>(sock_state_e.not_connected);
		public SyncState<sock_send_state_e> m_send_state = new SyncState<sock_send_state_e>(sock_send_state_e.not_sending);
		public SyncState<sock_recv_state_e> m_recv_state = new SyncState<sock_recv_state_e>(sock_recv_state_e.not_receiving); //.net6 stackoverflow by joygram 2022/08/17

		gplat.LogicTimer m_heartbeat_timer = new gplat.LogicTimer();
		protected gplat.LogicTimer m_connect_timer = new gplat.LogicTimer();

		//heartbeat
		//전역 기본값 : 전체 정책을 변경하는 경우 사용 : by joygram 2022/07/14
		public static bool m_global_enable_heartbeat = true;
		public static bool m_global_heartbeat_disconnect = true; //하트 비트를 타임아웃 발생시 접속을 종료할 것인가?

		//
		public bool m_enable_heartbeat = true;
		public bool m_heartbeat_disconnect = true;



		public static readonly Int32 SOCKET_HEARTBEAT_GAUGE = (Int32)gplat_define.socket_e.SOCKET_HEARTBEAT_GAUAGE;
		protected int m_socket_gauage = SOCKET_HEARTBEAT_GAUGE;

		protected Thread m_timer_thread;
		protected gplat.Result m_gen_result = new gplat.Result();

		//서버용 세션정보 보정 by joygram 2022/08/19
		public UInt64 m_channel_session_id; //서버용 세션에서 사용
		public Int32 m_server_id; //서버용 세션 로직 서버 자신의 id
		public Int64 m_auth_dbid; //서버용 세션 register한 클라이언트의 auth_dbid
		public bool m_header_toucherble = false; //헤더 정보 보정 요부 for client session by joygram 2022/08/22
		public bool m_use_relay_to_message = true; //월드로직은 사용하지 않는다. 



		public string remoteIp => m_raw_socket.remoteIp();

		//-- virtual method --//
		public virtual void onCreateTimers() { }
		public virtual void onCancelTimers() { }
		public virtual void onConnectionReset() { }
		public virtual void onHeartbeatTimeout() { }
		public virtual void onCreateRawSocket() { }

		//[memory]
		public void Dispose()
		{
			cancelTimers();
			// 모든 버퍼 초기화 기능 필요 
			m_send_failed_packets.clear();

			m_message_notifier.clearMessageQueues();//큐내용 모두제거 기능 추가 by joygram 2022/11/01
			GC.SuppressFinalize(this);
		}

		public TcpSocket()
		{
			m_sock_type = sock_type_e.standard;
			createTimers();
			registerSocketMessageReceiver();
		}

		~TcpSocket()
		{
			Dispose();
		}


		//
		public string connectionString()
		{
			return String.Format("{0}:{1}", m_hostname, m_port);
		}


		//protected void createRawSocket(string ip, UInt16 port)
		public gplat.Result createRawSocket(string in_hostname, UInt16 in_port)
		{
			var gen_result = new gplat.Result();
			lock (m_socket_mutex)
			{
				//m_disposed = false;
				m_raw_socket?.close();

				m_hostname = in_hostname;
				m_port = in_port;

				IPAddress[] ip_addrs;

				try
				{
					ip_addrs = Dns.GetHostAddresses(in_hostname);
				}
				catch (Exception ex)
				{
					return gen_result.setFail(result.code_e.GPLAT_CANT_RESOLVE_HOSTNAME, $"can not resolve hostname:{in_hostname}, {ex.Message}");
				}

				m_raw_socket = new RawSocket(ip_addrs[0], in_port);
				m_socket_gauage = SOCKET_HEARTBEAT_GAUGE;

				onCreateRawSocket();
				m_send_state.setState(sock_send_state_e.not_sending);
				m_sock_state.setState(sock_state_e.disconnected);
			}
			return gen_result.setOk();
		}
		public void registerMessageReceiver(gplat.MessageReceiver in_message_receiver)
		{
			m_message_notifier.addMessageReceiver(in_message_receiver);
		}
		protected void registerSocketMessageReceiver()
		{
			if (m_socket_message_receiver_registered)
			{
				return;
			}
			m_socket_message_receiver_registered = true;

			m_socket_message_reciver.setHandleDirect(true);
			m_socket_message_reciver.registerHandler(msg_gen_network.id_e.notify_heartbeat, handler_network_notify_heartbeat);

			m_message_notifier.addMessageReceiver(m_socket_message_reciver);
		}
		public bool isState(sock_state_e in_sock_state)
		{
			return m_sock_state.isState(in_sock_state);
		}

		protected void onDisconnected(ref RawSocket in_raw_socket, gplat.Result in_disconnect_result)
		{
			if (m_sock_state.isState(sock_state_e.disconnected))
			{
				m_log.Warn("already disconnected");
				return;
			}
			try
			{
				gplat.Log.logger("socket").Info($"disconnect EVENT occurred:{in_disconnect_result}");

				sock_state_e out_old_state;
				if (false == m_sock_state.exchangeNotEqualExcept(sock_state_e.disconnected, sock_state_e.connecting, out out_old_state))
				{
					m_log.Warn($"skip reason state:{out_old_state}. already by {in_raw_socket.m_disconnect_result}");
					return;
				}
				//패킷 전송시 자동 접속을 시도하므로 하트비트 타이머를 통한 재연결은 사용하지 않도록 변경. by jogyram 2020/08/17
				cancelTimers();
				stopTimerThread();

				in_raw_socket.m_disconnect_result = in_disconnect_result;
				in_raw_socket.close();

				notifySocketClosed(); // disconnected
			}
			catch (Exception ex)
			{
				m_log.Error($"{ex}");
			}
		}


		//사용하는 용도는 더이상 재접속을 하지 않겠다는 의미로 받아들임.
		public void closeSocket(ref RawSocket in_raw_socket, gplat.Result in_disconnect_result, SocketError in_socket_error = SocketError.Success)
		{

			if (m_sock_state.isState(sock_state_e.disconnected))
			{
				m_log.Warn("already closed");
				return;
			}
			try
			{
				if (isInternal)//#virtualserver
				{
					return;
				}
				stopTimerThread();
				cancelTimers(); //의도적으로 접속을 종료하였으므로 재접속 시도를 하지 않는다.
				m_message_notifier.clearMessageQueues(); //패킷큐 모두 제거 by jogyram 2022/11/01
				sock_state_e old_state;
				if (false == m_sock_state.exchangeNotEqual(sock_state_e.disconnected, out old_state))
				{
					m_log.Warn($"skip reason state:{old_state}. already by {in_raw_socket.m_disconnect_result}");
					if (sock_state_e.disconnected == old_state)
					{
						//이미 접속 종료됨, 원인은 무엇이다. 로그를 찍어줍니다.
					}
					return;
				}
				if (in_raw_socket != null)
				{
					in_raw_socket.m_disconnect_result = in_disconnect_result;
					in_raw_socket.close();
				}
				notifySocketClosed(); //스스로 접속을 종료했을 경우, stopped
				if (in_disconnect_result.fail())
				{
					m_log.ErrorFormat("DISCONNECT REASON:{0}", in_disconnect_result.ToString());
				}
			}
			catch (Exception ex)
			{
				m_log.Debug($"fail disconnect: {ex}");
			}
		}

		public void closeSocket(gplat.Result in_disconnect_result, SocketError in_socket_error = SocketError.Success)
		{
			closeSocket(ref m_raw_socket, in_disconnect_result, in_socket_error);
		}

		public void startSocket()
		{
			if (isInternal)
			{
				return;
			}

			beginSend();
			beginReceive();
		}



		protected gplat.Result beginReceive()
		{
			var result = new gplat.Result();
			if (!m_sock_state.isState(sock_state_e.connected))
			{
				m_log.Debug("socket is not connected. can not receive.");
				return result.setOk();
			}

			return m_raw_socket.beginReceive(new AsyncCallback(onEndReceive));
		}

		public void onEndReceive(IAsyncResult in_async_result)
		{
			if (false == m_sock_state.isState(sock_state_e.connected))
			{
				gplat.Log.logger("gplat").Warn("socket already disconnected. no more receive.");
				return;
			}

			var gplat_result = new gplat.Result();
			var raw_socket = (RawSocket)in_async_result.AsyncState;
			try
			{
				Int32 received_size = 0;
				gplat_result = m_raw_socket.endReceive(in_async_result, out received_size);
				if (gplat_result.ok())
				{
					while (true)
					{
						var packet = m_raw_socket.takePacket(ref gplat_result);
						if (gplat_result.fail())
						{
							m_log.Error($"invalid packet: {gplat_result}");
							//엔진버젼, 잘못된 패킷을 수신한 경우 : 클라이언트를 종료 or 유지.
							if (gplat_result.isResultCode(result.code_e.GPLAT_INVALID_PACKET))
							{
								//받은 쪽에 invalid 패킷이 수신되었음을 내부에 알리는 메시지 정의 by joygram 2020/11/19
								notifyInvalidPacket(gplat_result);
							}
							break;
						}
						if (null == packet)
						{
							break;
						}

						touchHeader(packet);
						relayToMessage(packet); //릴레이 패킷 보정 by joygram 2022/08/25

						packet.m_socket = this; //받은 소켓 기반 처리가 되도록 함. 

						var notified = m_message_notifier.notify(packet);
						if (!notified)
						{
							m_log.Fatal($"message {gplat.Message.toDetail(packet.messageId())} not notified! handler required");
						}
					}
				}
			}
			catch (Exception ex)
			{
				gplat_result.setExceptionOccurred(result.code_e.GPLAT_SOCKET_RECV_EXCEPTION_OCCURRED, ex.ToString());
				gplat.Log.logger("socket").Error($"{ex.Message}"); //for check
			}
			finally
			{
				if (gplat_result.ok())
				{
					//m_recv_state.setState(sock_recv_state_e.not_receiving);
					gplat_result = beginReceive();
				}
				else
				{
					onDisconnected(ref raw_socket, gplat_result);
				}
			}
		}

		// 보낸 패킷 제거, 전송 상태 풀어줌. 
		protected void completeSend()
		{
			m_send_packet_queue.pop();
			m_send_state.setState(sock_send_state_e.not_sending);
		}

		void touchHeader(GenPacket in_packet)
		{
			if (false == m_header_toucherble)
			{
				return;
			}
			//register시 포함된 세션정보 넣어주기
			//appsessionguid 
			in_packet.m_packet_header.m_auth_id = (UInt64)m_auth_dbid;
			in_packet.m_packet_header.m_session_id = m_channel_session_id;
			in_packet.m_packet_header.m_server_id = (UInt16)m_server_id;
		}
		//릴레이 메시지 보정 
		void relayToMessage(GenPacket in_packet)
		{
			if (false == m_use_relay_to_message)
			{
				return;
			}

			//릴레이 패킷을 받은 경우 원래 패킷으로 복원, 채널을 통과하지 않고 전송하는 경우 보완 by joygram 2022/08/09 
			if (in_packet.isRelayMessage())
			{
				in_packet.relayToMessage();
			}
		}

		public override bool sendPacket(GenPacket in_packet, bool in_sync_send = false)
		{
			//패킷 재사용시 문제가 되는 변수는 초기화 시켜주도록 한다. 
			in_packet.m_send_retry_count = 0;

			if (isInternal)
			{
				//relay id 인경우 복구 
				if (in_packet.isRelayMessage())
				{
					in_packet.m_packet_header.relayToMessage();
				}

				in_packet.m_socket = this;

				if (isSockType(sock_type_e.local_logic))
				{
					return InternalNotifier.local_it.sendPacket(in_packet);
				}
				else
				{
					return InternalNotifier.it.sendPacket(in_packet);
				}
			}
			else if (true == in_sync_send)
			{
				return sendInternal(in_packet, in_sync_send);
			}
			else // queue에 넣은 후 비동기 전송 
			{
				m_send_packet_queue.push(in_packet);
				return beginSend();
			}
		}

		bool beginSend()
		{
			var gen_result = new gplat.Result();
			if (null == m_raw_socket)
			{
				m_log.Error("m_raw_socket is null");
				prepareOfflineSend();
				return false;
			}

			// 전송시도
			sock_send_state_e out_old_state;
			if (false == m_send_state.exchangeNotEqual(sock_send_state_e.sending, out out_old_state))
			{
				m_log.Debug("sending I/O thread, already. just return");
				return true;
			}

			var sending_packet = m_send_packet_queue.peek();
			if (null == sending_packet) //더이상 보낼 것이 없으면 전송 중 락 풀기 
			{
				m_send_state.setState(sock_send_state_e.not_sending);
				return false;
			}
			return sendInternal(sending_packet);
		}

		private bool processOfflineSend(GenPacket in_sending_packet)
		{
			var gen_result = new gplat.Result();

			//하트비트는 재전송하지 않는다.
			if (in_sending_packet.isMessageId(msg_gen_network.id_e.notify_heartbeat))
			{
				completeSend(); //전송큐에서 제거  
				prepareOfflineSend(); //커넥터인 경우 재접속 시도, 서버세션인 경우가 생기면 별개처리를 수행해야함. (세션제거) 
				return true;
			}


			if (in_sending_packet.m_send_retry_count >= (Int16)gplat_define.socket_e.SOCKET_MAX_RESEND_COUNT)
			{
				m_log.DebugFormat($"max send_retry_count:{in_sending_packet.m_send_retry_count} over");
				completeSend(); //전송큐에서 제거
				notifySendFail(in_sending_packet, gen_result.setFail(result.code_e.GPLAT_SOCKET_RESEND_OVERFLOW, $"retry count over:{in_sending_packet.m_send_retry_count}, {gplat_define.socket_e.SOCKET_MAX_RESEND_COUNT}")); //별도로 로그를 남긴다. 
				return true;
			}
			m_log.Warn($"{m_sock_state}, {in_sending_packet.detailMessageIdStr},{in_sending_packet.detailRelayMessageIdStr}, packet retried:{in_sending_packet.m_send_retry_count}");

			in_sending_packet.m_send_retry_count++;
			if (m_sock_state.isState(sock_state_e.connecting)) // 접속 시도 중이면 가만히 리턴
			{
				m_log.Warn("connecting already. just return");
				m_send_state.setState(sock_send_state_e.not_sending);
				return false;
			}

			prepareOfflineSend();
			return false;
		}

		protected void onSendFail(GenPacket in_sending_packet, gplat.Result in_gen_result)
		{
			m_log.Warn(in_gen_result.ToString());
			notifySendFail(in_sending_packet, in_gen_result);
			onDisconnected(ref m_raw_socket, in_gen_result); //접속종료
		}
		protected bool sendInternal(GenPacket in_sending_packet, bool in_sync_send = false)
		{
			var gen_result = new gplat.Result();
			if (!m_sock_state.isState(sock_state_e.connected)) // use check callback
			{
				return processOfflineSend(in_sending_packet); //접속이 안된 상태라면 무조건 접속을 수행 후 전송하도록 변경 by joygram 2020/10/08
			}

			// 패킷 후 가공 처리 : 암호화 / 압축 
			in_sending_packet.m_packet_header.m_sequence = (++m_send_sequence) & 0xffffffff; //sequence warp around
			if (m_use_encryption)
			{
				gen_result = in_sending_packet.encrypt(); //재전송 처리가 제대로 되는지 확인 (c#은 중복 수행시 같은 객체이기 때문에 이슈가 됨) 
				if (gen_result.fail())
				{
					m_log.Error(gen_result.ToString());
					completeSend(); //전송큐에서 보낼 패킷을 제거함.
					return false;
				}
			}

			if (in_sync_send) // send sync, not use queue 
			{
				m_log.Debug("try sync send");
				gen_result = m_raw_socket.send(in_sending_packet);
				if (gen_result.fail())
				{
					onSendFail(in_sending_packet, gen_result);
				}
			}
			else if (m_use_async_send) //begin send async, use queue
			{
				gen_result = m_raw_socket.sendAsync(in_sending_packet, new AsyncCallback(onEndSend));
				if (gen_result.fail())
				{
					completeSend(); //전송큐에서 보낼 패킷을 제거함.
					onSendFail(in_sending_packet, gen_result);
					prepareOfflineSend();// 전송 실패시 연결을 복원 하도록 한다 by jogyram 2020/11/12  
					return false;
				}
			}
			else //begin send sync, use queue 
			{
				gen_result = m_raw_socket.send(in_sending_packet);
				completeSend();
				if (gen_result.fail())
				{
					onSendFail(in_sending_packet, gen_result);
					return false;
				}
			}
			return true;
		}

		public virtual void prepareOfflineSend() //접속 종료 상태에서 전송하려고 하는 경우 액션, connector의 경우 재접속을 한다.
		{

		}

		// 아직 전송 패킷 매니져는 사용안하고 있음.
		public void onEndSend(IAsyncResult in_async_result)
		{
			var gen_result = new gplat.Result();
			GenPacket gen_packet = null;
			var raw_socket = (RawSocket)in_async_result.AsyncState;
			try
			{
				Int32 send_bytes = 0;
				gen_result = raw_socket.endSend(in_async_result, out send_bytes);
				return;
			}
			catch (SocketException sock_ex)
			{
				gen_result.setExceptionOccurred(result.code_e.GPLAT_SOCKET_RECV_EXCEPTION_OCCURRED, $"send socket error:[{sock_ex.ErrorCode}:{sock_ex.SocketErrorCode}]{sock_ex.Message}");
			}
			catch (Exception ex)
			{
				if (ex is ObjectDisposedException || ex is ArgumentException)
				{
					m_log.DebugFormat("socket already destroyed. ObjectDisposedException silently skip.");
					return;
				}
				gen_result.setExceptionOccurred(result.code_e.GPLAT_SOCKET_RECV_EXCEPTION_OCCURRED, ex.ToString());
			}
			finally
			{
				completeSend();

				if (gen_result.ok())
				{
					beginSend();
				}
				else
				{
					notifySendFail(gen_packet, gen_result);
					onDisconnected(ref raw_socket, gen_result);
					m_log.ErrorFormat("send error: {0}", gen_result.ToString());
				}
			}
		}
		public bool send(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg, PacketHeader in_src_header = null, bool in_send_sync = false)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);
			packet.m_packet_header.copySessionInfoFrom(in_src_header);

			return sendPacket(packet, in_send_sync);
		}
		public bool send(GenPacket in_packet, bool in_send_sync = false)
		{
			return sendPacket(in_packet, in_send_sync);
		}

		public GenPacket popSendFailedPacket(string in_manage_guid)
		{
			GenPacket gen_packet;
			m_send_failed_packets.find(in_manage_guid, out gen_packet);
			if (null == gen_packet)
			{
				m_log.DebugFormat("manage_guid:{0} packet is not exist.", in_manage_guid);
			}
			return gen_packet;
		}
		public bool sendBack(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg, PacketHeader in_src_header, bool notify_direct = false)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);

			packet.m_packet_header.copySessionInfoFrom(in_src_header);
			return sendPacket(packet);
		}

		public bool relay(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg, PacketHeader in_src_header = null)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);
			return relay(packet, in_src_header);
		}
		public bool relay(GenPacket in_packet, PacketHeader in_src_header)
		{
			in_packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_packet);
			in_packet.m_packet_header.copySessionInfoFrom(in_src_header);

			m_log.Debug($"relay_session_id:{in_packet.m_packet_header.m_relay_session_id}, auth_id:{in_packet.m_packet_header.m_auth_id}");
			return sendPacket(in_packet);
		}

		public bool relayAnonymous(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg)
		{
			GenPacket gen_packet = new GenPacket();
			gen_packet.fromNetMsg(in_msg_id, in_net_msg);
			gen_packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_anonymous);
			return sendPacket(gen_packet);
		}
		public bool relayToCommunity(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg, PacketHeader in_src_header = null)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);
			packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_community);
			packet.m_packet_header.copySessionInfoFrom(in_src_header);
			return sendPacket(packet);
		}
		public bool relayToGame(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg, PacketHeader in_src_header = null)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);
			packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_game);
			packet.m_packet_header.copySessionInfoFrom(in_src_header);
			return sendPacket(packet);
		}
		public bool relayToServer(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg, PacketHeader in_src_header = null)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);
			packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_server);
			packet.m_packet_header.copySessionInfoFrom(in_src_header);
			return sendPacket(packet);
		}
		public bool relayToServer(GenPacket in_packet, PacketHeader in_src_header = null)
		{
			in_packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_server);
			in_packet.m_packet_header.copySessionInfoFrom(in_src_header);
			return sendPacket(in_packet);
		}
		//현재 채널에만 
		public bool relayBroadcast(Int32 in_msg_id, Thrift.Protocol.TBase in_net_msg)
		{
			GenPacket packet = new GenPacket();
			packet.fromNetMsg(in_msg_id, in_net_msg);
			packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_broadcast_packet);
			return sendPacket(packet);
		}
		//현재 채널에만 
		public bool relayBroadcast(Int32 in_msg_id, GenPacket in_packet)
		{
			in_packet.messageToRelay((UInt32)msg_gen_network.id_e.relay_broadcast_packet);
			return sendPacket(in_packet);
		}

		public bool isConnected()
		{
			return m_sock_state.isState(sock_state_e.connected);
		}


		public virtual void onProcessTimer() { }

		public void processTimer(object tcp_socket_object)
		{
			var tcp_socket = (TcpSocket)tcp_socket_object;
			while (tcp_socket.m_timer_thread_active)
			{
				Thread.Sleep(1000);
				if (m_heartbeat_timer.m_active)
				{
					if (m_heartbeat_timer.expired())
					{
						handleHeartbeatTimer();
						m_heartbeat_timer.reset();
					}
				}
				tcp_socket.onProcessTimer();
			}
		}

		//timers
		void createTimers()
		{
			m_heartbeat_timer.setTimer((Int32)gplat_define.socket_e.SOCKET_HEARTBEAT_INTERVAL);

			onCreateTimers();
		}

		protected void startTimerThread()
		{
			stopTimerThread();
			m_timer_thread_active = true;
			m_timer_thread = new Thread(new ParameterizedThreadStart(this.processTimer));
			m_timer_thread.Start(this);
		}

		protected void stopTimerThread()
		{
			//기존 쓰레드를 죽임 
			if (m_timer_thread_active && m_timer_thread != null)
			{
				m_timer_thread_active = false;
				m_timer_thread.Join();
			}
		}

		protected void cancelTimers()
		{
			m_timer_thread_active = false; // 타이머 쓰레드를 죽임... by redsun01
			cancelHeartbeatTimer();
			onCancelTimers();
		}
		public void handleHeartbeatTimer()
		{
			if (false == m_timer_thread_active)
			{
				return;
			}

			if (m_socket_gauage <= 0)
			{
				m_log.Error("network not responsible. notify heartbeat timeout."); //로그 오류: 네트워크 오류, 상대로부터 일정시간 응답이 없었다;
				m_socket_gauage = SOCKET_HEARTBEAT_GAUGE;

				if (m_heartbeat_disconnect)
				{
					onDisconnected(ref m_raw_socket, (new gplat.Result()).setFail(result.code_e.GPLAT_SOCKET_HEARTBEAT_TIMEOUT, "heartbeat timeout. It may be disconnected."));
				}
				return;
			}
			m_socket_gauage--;
			gplat.Log.logger("socket." + m_log.Logger.Name).Debug($"socket_gauage:{m_socket_gauage}");

			var notify_heartbeat = new msg_gen_network.notify_heartbeat();
			send((Int32)msg_gen_network.id_e.notify_heartbeat, notify_heartbeat);
		}

		public void setHeartbeatTimer()
		{
			if (false == m_enable_heartbeat)
			{
				return;
			}
			m_heartbeat_timer.activate();
		}
		public void cancelHeartbeatTimer()
		{
			m_heartbeat_timer.deactivate();
		}

		// 하트비트를 상대방에게 받으면 게이지 충전
		public void handler_network_notify_heartbeat(GenPacket gen_packet)
		{
			m_socket_gauage = SOCKET_HEARTBEAT_GAUGE;
			m_log.Debug("received network_notify_heartbeat");
		}
	}
}
