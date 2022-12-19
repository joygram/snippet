
using log4net;

using System;

// receiver들에게 메시지를 전달하는 전달자
public class MessageNotifier
{
	ILog m_log = gplat.Log.logger("message.notifier");
	string m_logger_name = "message.notifier";

	public MessageReceiverList m_message_receivers = new MessageReceiverList();

	gplat_define.notifier_type_e m_notifier_type = gplat_define.notifier_type_e._BEGIN;

	public MessageNotifier(gplat_define.notifier_type_e in_notifier_type, string in_logger_name = "message.notifier")
	{

		setLogger(in_logger_name);
		setNotifierType(in_notifier_type);
	}
	public void setLogger(string in_logger_name)
	{
		m_log = gplat.Log.logger(in_logger_name);
		m_logger_name = in_logger_name;
	}
	public void setNotifierType(gplat_define.notifier_type_e in_notifier_type)
	{
		m_notifier_type = in_notifier_type;
	}

	public bool notify(GenPacket in_packet, bool notify_direct = false)
	{
		bool notified = false;
		var handle_message_id = in_packet.messageId();

		// receiver 에 GenPacket 전달전에 현재 Notifier의 타입을 구분할 수 있도록 함. 
		in_packet.setPacketNotifierType(m_notifier_type);

		var receivers = m_message_receivers.receivers();
		for (int i = 0; i < receivers.Count; ++i)
		{
			var receiver = receivers[i];

			if (receiver.processable(in_packet))
			{
				//m_log.Error($"Message notify [{receiver.loggerName}] {receiver.GetType().FullName} handle {in_gen_packet.m_packet_header.m_message_id}");
				notified = receiver.handleNotify(in_packet, notify_direct);
			}
		}
		// LOG THIS POINT
		return notified;
	}

	public bool notify(Int32 in_msg_id, Thrift.Protocol.TBase in_gen_msg, bool in_notify_direct = false)
	{
		var gen_packet = new GenPacket();
		gen_packet.fromNetMsg(in_msg_id, in_gen_msg);

		return notify(gen_packet, in_notify_direct);
	}

	public void addMessageReceiver(gplat.MessageReceiver in_message_receiver) //처리객체 등록
	{
		//m_log.Warn($"{m_logger_name} Message Receiver ADDED: {in_message_receiver.GetType()}, logger:{in_message_receiver.loggerName}");
		m_message_receivers.addMessageReceiver(in_message_receiver);
	}

	public void removeMessageReceiver(gplat.MessageReceiver in_message_receiver)
	{
		m_message_receivers.removeMessageReceiver(in_message_receiver);
	}

	//메시지 리시버에 포함되어 있는 모든 메시지를 제거한다. by jogyram 2022/11/01
	public void clearMessageQueues()
	{
		m_message_receivers.clearMessageQueues();
	}
}
