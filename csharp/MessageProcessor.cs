using log4net;

using System;

// 패킷 핸들러를 동적생성 처리를 진행함. 
// 생성 제거의 dispose()구현 필요 
namespace gplat
{
	public class MessageProcessor<T> where T : new()
	{
		public SuspendHandlerManager m_suspend_handler_mgr = SuspendHandlerManager.it; //#suspendhandler 를 외부 지정할 수 있도록 추가 by joygram 2021/01/29
		ILog m_log = gplat.Log.logger("message.processor");
		gplat.Profiler m_profiler = null;

		UInt32 m_msg_handler_id = 0;

		public MessageProcessor()
		{
		}

		public UInt32 messageHandlerId()
		{
			if (0 == m_msg_handler_id)
			{
				//todo reflection으로 변경 
				var handler = (IMessageHandler)new T();
				m_msg_handler_id = handler.reqMsgId();
			}
			return m_msg_handler_id;
		}

		gplat.Result safePrepare(IMessageHandler in_message_handler, GenPacket in_packet)
		{
			gplat.Result gplat_result = new gplat.Result();
			try
			{
				using (NDC.Push(in_message_handler.logInfo()))
				{
					gplat_result = in_message_handler.prepare(in_packet);
				}
			}
			catch (Exception ex)
			{
				gplat.Log.logger("exception").Fatal($"Message:[{gplat.Message.toString(in_message_handler.reqMsgId())}] [{ex}]");

				in_message_handler.setResult(gplat_result.setExceptionOccurred(ex.ToString()));
				NDC.Clear();
			}
			return gplat_result;
		}

		gplat.Result safeProcess(IMessageHandler in_message_handler)
		{
			gplat.Result gplat_result = new gplat.Result();
			try
			{
				using (NDC.Push(in_message_handler.logInfo()))
				{
					in_message_handler.logReqMsg();
					gplat_result = in_message_handler.process();
				}
			}
			catch (Exception ex)
			{
				gplat.Log.logger("exception.processor").Fatal($"HANDLER EXCEPTION {in_message_handler.GetType()}, {Message.toDetail(in_message_handler.reqMsgId())}, {ex.Message}, {ex.Source}");

				in_message_handler.setResult(gplat_result.setExceptionOccurred(ex.ToString()));
				NDC.Clear();
			}
			return gplat_result;
		}

		void safeCleanup(IMessageHandler in_handler)
		{
			try
			{
				using (NDC.Push(in_handler.logInfo()))
				{
					//in_handler.onCleanup();
					in_handler.cleanup();
					in_handler.afterCleanup();
					in_handler.Dispose(); //
				}
			}
			catch (Exception ex)
			{
				gplat.Log.logger("exception").Fatal($"[MessageProcessor:{GetType()}] Message:[{Message.toString(in_handler.reqMsgId())}] [{ex}]");
				NDC.Clear();
			}
		}

		gplat.Result safeResume(IMessageHandler in_handler, GenPacket in_packet)
		{
			gplat.Result gen_result = new gplat.Result();
			try
			{
				using (NDC.Push(in_handler.logInfo()))
				{
					gen_result = in_handler.processResume(in_packet);
				}
			}
			catch (Exception ex)
			{
				in_handler.setResult(gen_result.setExceptionOccurred(string.Format($"RESUME EXCEPTION: handler{in_handler.GetType().Name} {ex}")));
				NDC.Clear();
			}
			return gen_result;
		}


		void processHandler(IMessageHandler in_handler, GenPacket in_packet)
		{
			m_profiler = new gplat.Profiler(in_handler.GetType().FullName);

			m_profiler.begin(0.1);

			var gplat_result = safePrepare(in_handler, in_packet);
			if (gplat_result.fail()) // prepare실패인 경우 오류 처리 
			{
				safeCleanup(in_handler);
				m_profiler.end();
				return;
			}
			else if (gplat_result.alreadyProcessed()) //이미 처리한 경우 내부 자체 처리가 있고 여기서는 종료 by joygram 2020/04/07
			{
				m_profiler.end();
				return;
			}

			gplat_result = safeProcess(in_handler);
			if (gplat_result.exceptionOccurred())
			{
				safeCleanup(in_handler);
				m_profiler.end();
				return;
			}

			if (in_handler.isHandlerState(message_handler_state_e.handler_suspended)) // suspend 상태일 경우 cleanup 수행을 잠시 보류
			{
				return;
			}

			safeCleanup(in_handler);
			m_profiler.end();
		}

		// return processed 
		bool processSuspendHandler(MessageHandler in_suspend_handler, GenPacket in_packet)
		{
			if (null == in_suspend_handler)
			{
				return false;
			}

			var packet_guid_str = in_packet.m_packet_header.guid().ToString();

			if (in_packet.isPacketNotifierType(gplat_define.notifier_type_e.SERVER_SOCKET))
			{
				//중지된 핸들러가 있는 상황에서 서버측 소켓에서 처리요청이 들어옴 : by joygram 2020/04/07
				//서버측 소켓은 채널서버이며 [중지된 상황]에서 [서버소켓]에서 요청이 들어온 것은 [재(중복)요청]으로 판단할 수 있음  
				notifyNotComplete(in_packet);
				m_log.Debug($"{in_packet.detailMessageIdStr} / {packet_guid_str} is processing. not completed.");
				return true;
			}

			switch (in_suspend_handler.m_handler_state)
			{
				case message_handler_state_e.handler_suspended: // suspend_processor가 없는 경우 기존 핸들러를 처리할 수 있도록 상태만 변경 
					m_log.Debug($"[{packet_guid_str}] {in_packet.detailMessageIdStr} handler_suspended -> handler_resumable");
					in_suspend_handler.m_handler_state = message_handler_state_e.handler_resumable; //최종 처리가 가능하도록 상태 변경 
					if (null != in_suspend_handler.m_suspend_processor)
					{
						m_log.Debug($"[{packet_guid_str}] {in_packet.detailMessageIdStr} {in_suspend_handler.loggerName}:{in_suspend_handler.m_suspend_processor.GetType().Name} process ");

						//exception생길 경우 처리 순서 확인, cleanup으로 나와서 처리가 완료되는지 확인 필요 by joygram 2020/04/06 
						processHandler(in_suspend_handler.m_suspend_processor, in_packet);
						return true;
					}
					break;

				case message_handler_state_e.handler_resumable:
					m_log.Debug($"[{packet_guid_str}] handler_resumable safeResume: {in_packet.detailMessageIdStr}");
					var resume_result = safeResume(in_suspend_handler, in_packet);
					if (resume_result.notComplete())
					{
						m_log.InfoFormat($"[{packet_guid_str}]resumble notComplete {in_packet.detailMessageIdStr}");
						return true;
					}
					m_suspend_handler_mgr.popHandler(in_packet.m_packet_header.guid().ToString());
					safeCleanup(in_suspend_handler);
					return true;

				default:
					m_log.Error($"unknown handler's state");
					return true;
			}
			return false;
		}
		// 핸들러 객체를 사용한 후 제거 되도록 할 필요가 있음 : dispose의 의미 체크 
		public void process(GenPacket in_packet)
		{
			try
			{
				var packet_guid_str = in_packet.m_packet_header.guid().ToString();

				//여기까지 들어왔다는 것 관련된 packet message_id가 등록되어 있는 receiver가 호출 되었다는 의미임.
				m_log.Debug($"PROCESS {packet_guid_str} {in_packet.detailMessageIdStr}");
				var suspend_handler = m_suspend_handler_mgr.findHandler(packet_guid_str);
				if (processSuspendHandler(suspend_handler, in_packet))
				{
					return;
				}

				//message type에 맞는 핸들러 생성 및 실행 : suspend processor의 경우 여기서 unhandled 핸들러를 생성, message_id기반(suspend_handler)은 unhandled가 기본으로 등록됨.
				var new_handler = (IMessageHandler)new T();
				m_log.Debug($"try process {packet_guid_str} {in_packet.detailMessageIdStr} {new_handler.GetType().Name} ");
				processHandler(new_handler, in_packet);
			}
			catch (Exception ex)
			{
				gplat.Log.logger("exception").Fatal($"[Exception]{ex}");
			}
		}

		private static void notifyNotComplete(GenPacket in_packet)
		{
			gplat.Log.logger("message.processor").Warn($"notify not complete :{in_packet.messageIdString()}");

			msg_gen_network.notify_notcomplete notify = new msg_gen_network.notify_notcomplete();
			notify.MsgInfo.MsgResult.ResultType = gplat_define.result_type_e.Notcomplete;
			notify.Guid = in_packet.m_packet_header.guid().ToString();

			PacketHeader header = new PacketHeader();
			header.m_auth_id = in_packet.m_packet_header.m_auth_id;
			header.m_session_id = in_packet.m_packet_header.m_session_id;
			header.m_relay_session_id = in_packet.m_packet_header.m_relay_session_id;
			header.setGuid(Guid.NewGuid());

			in_packet.m_socket.relay(notify.MsgInfo.MsgId, notify, header);
		}
	}
}
