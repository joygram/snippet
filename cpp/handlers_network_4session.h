#pragma once
#include "GameSessionHandler.h"
#include <libGen/cpp/network/Connection.h>
#include <libServer/cpp/UserManager.h>
#include "GameServer.h"
#include "GameUser.h"
#include "GameSessionHandler.h"
#include "GameSession.h"
#include <result_code_types.h>

namespace handler
{
	//별다른 기능을 수행하지 않도록 변경 by joygram 2020/11/16
	struct network_notify_socket_connected_4session
		: public GameSessionHandlerT<msg_gen_network::notify_socket_connected>
	{
		void setup() override
		{
			m_prepare_session_user = false;
		}
		gplat::Result process() override
		{
			auto notify = PacketToNetMsg<handle_message_t>(m_packet);
			LOG_INFO("{} session_id:{} connected", NetMsgToStr(*notify), notify->session_id);
			return m_result.setOk();
		}
		void cleanup() override
		{
			if (m_result.fail())
			{
				LOG_ERROR(m_result.toString());
			}
		}
	};

	struct network_notify_socket_closed_4session
		: public GameSessionHandlerT<msg_gen_network::notify_socket_closed>
	{
		gplat::Result process() override
		{
			LOG_WARN("{}", gameSession()->errorMessage());

			auto notify = PacketToNetMsg<handle_message_t>(m_packet);
			LOG_INFO("{}", NetMsgToStr(*notify));

			auto session_type = gameSession()->m_session_type;

			if (session_type_e::server == session_type)
			{
				int32_t server_id = m_logic_server->m_server_info.serverId;

				LOG_ERROR("logic server session closed server_id/{}/", server_id);

				gameServer()->unregisterServer(server_id);

				//다른 서버에 로직서버가 끊어진걸 알림.(notify_server_unregister)
				gameServer()->notifyUnregisterServer(server_id);

				//유저에게 알림 제거, 사용자가 다른 방식으로 극복이 가능함 : by joygram 2020/11/18
				//notifyServerShutdownToUsers(server_id);

			}
			else if (session_type_e::user == session_type)
			{
				if (logicServer())
				{
					// user session이 끊어짐을 알려 준다.
					msg_gen_manage::req_client_unregister req;
					req.channelSessionId = notify->session_id;

					auto game_user = gameSession()->gameUser();
					req.authId = game_user->m_account_db_id;

					logicServer()->send(req);
				}
			}
			userManager()->deleteUserBySessionId(notify->session_id);
			return m_result.setOk();
		}

		void notifyServerShutdownToUsers(int32_t in_server_id)
		{
			//로직서버와의 연결이 단절 되었음. 사용자에게 알림 
			auto session_list = gameServer()->sessionManager()->toVector();
			for (auto session : session_list) //session broadcast & logic server reset
			{
				auto game_session = boost::static_pointer_cast<GameSession>(session);
				auto game_user = game_session->gameUser();
				if (!game_user)
				{
					continue;
				}
				auto user_logic_server = game_user->logicServer();
				if (!user_logic_server)
				{
					continue;
				}
				if (in_server_id == user_logic_server->m_server_info.serverId)
				{
					game_user->m_logic_server.reset();

					gplat::Result notify_result;
					notify_result.setFail(result::code_e::GPLAT_LOGIC_SERVER_DISCONNECTED, sformat("logic server:{0} shutdown", in_server_id));

					//로직서버 해제 되었음. - 클라이언트는 필요시 재연결이 가능하게 변경되었으므로 별도 알림을 수행하지 않고 로그만 남기도록 변경 by joygram 2020/11/18 
					msg_gen_network::notify_system_error notify;
					notify.msgInfo.msgResult = gplat::toMsgResult(notify_result);
					game_user->sendToUser(notify);
				}
			}
		}

		void cleanup() override
		{
			if (m_result.fail())
			{
				LOG_ERROR(m_result.toString());
			}
		}
	};


	//게임 세션 정보 갱신을 수행함. : logic_server -> channel
	struct network_notify_user_session_info
		: public GameSessionHandlerT<msg_gen_network::notify_user_session_info>
	{
		gplat::Result process() override
		{
			auto notify = PacketToNetMsg<handle_message_t>(m_packet);
			LOG_INFO("{}", NetMsgToStr(*notify));

			boost::shared_ptr<GameSession> client_session;

			boost::shared_ptr<User> game_user;
			client_session = boost::static_pointer_cast<GameSession>(sessionManager()->getSessionById(notify->channel_session_id));
			if (!client_session)
			{
				return m_result.setFail(sformat("server_session:: message_id:{} not relayed, client session is not exist : {}", m_packet->packetHeader().messageId(), notify->channel_session_id));
			}

			//updaet session infos 
			client_session->m_logic_server_id = static_cast<uint16_t>(notify->logic_server_id); // lobby, field
			client_session->m_zone_server_id = static_cast<uint16_t>(notify->zone_server_id); // instant dungeon, room
			client_session->m_community_id = notify->community_id;
			client_session->m_guild_db_id = notify->guild_db_id;
			client_session->m_user_db_id = notify->user_db_id;

			LOG_INFO("client session info updated");
			return m_result.setOk();
		}

		void cleanup() override
		{
			if (m_result.fail())
			{
				LOG_ERROR(m_result.toString());
				msg_gen_network::notify_system_error notify;
				notify.msgInfo.msgResult = gplat::toMsgResult(m_result);
				auto res = send(notify);
			}
		}
	};
} //namespace handler
