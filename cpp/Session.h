//
#pragma once

#include <libGen/cpp/network/Socket.h>
#include <libGen/cpp/base/InstantId.h>
struct session_state_e
{
	enum type
	{
		session_closed,
		session_init,
		session_established,
		session_certified,
		session_logged_out
	};
};

class SessionManager;
class Server;
class Session
	: public Socket
{
public:
	Session(boost::shared_ptr<InstantId> session_instant_id, gplat::asio::io_context &io_context, boost::shared_ptr<boost::asio::ip::tcp::socket> sock, string_t logger_name)
		: Socket(io_context, sock, logger_name)
	{
		setSocket(this);
		m_session_instant_id = session_instant_id;
		setDefaultLoggerNdc(sformat("/new sessionId:{0}/", sessionId()));
		LOG_INFO("new session:{}", *m_session_instant_id);
	}

	Session(void);

	virtual ~Session(void);

public:
	boost::shared_ptr<Session> shared_from_this()
	{
		return boost::static_pointer_cast<Session>(Socket::shared_from_this());
	}
	boost::shared_ptr<Session const> shared_from_this() const
	{
		return boost::static_pointer_cast<Session const>(Socket::shared_from_this());
	}

public:
	void setSocket(SocketBase *socket);

	void onClose() override;

	void afterAddToManager();

public:
	gplat::Result init(SessionManager *in_session_manager);

	void touchHeader(boost::shared_ptr<Packet> &in_packet) override
	{
		//버퍼에서 가지와서 변경 후
		// in_packet->bufferToHeader();

		in_packet->packetHeader().setSessionId(*m_session_instant_id); // sessionId
		in_packet->packetHeader().setServerId(m_channel_server_id);	   // current server id

		// user session인 경우에만 넣어준다.
		if (session_type_e::user == m_session_type)
		{
			in_packet->packetHeader().setAuthDbId(m_account_db_id); //
																	// in_packet->packetHeader().copySessionGuid();
		}

		in_packet->headerToBuffer(); // 버퍼에 반영 릴레이 정보
	}

	session_state_e::type sessionState() const
	{
		return m_session_state;
	}

	void setSessionState(session_state_e::type in_session_state)
	{
		m_session_state = in_session_state;
	}

	bool isSessionState(session_state_e::type in_session_state) const
	{
		return sessionState() == in_session_state;
	}

public:
	void setCertified()
	{
		setSessionState(session_state_e::session_certified);
	}
	bool isCertified() const
	{
		return isSessionState(session_state_e::session_certified);
	}

public:
	void setSessionType(session_type_e::type session_type)
	{
		m_session_type = session_type;
		setDefaultLoggerNdc(sformat("/sessionId:{0}, session_type:{1}, account_db_id:{2}/", sessionId(), m_session_type, m_account_db_id));
	}

	bool isSessionType(session_type_e::type session_type)
	{
		return (m_session_type == session_type);
	}

public:
	const string_t &clientAddress() const
	{
		return m_client_address;
	}

	void setClientAddress(const string_t &client_addr)
	{
		m_client_address = client_addr;
	}
	void changeZoneServerId(const uint16_t zone_server_id)
	{
		m_zone_server_id = zone_server_id;
	}

protected:
	virtual gplat::Result afterInitSession();
	virtual gplat::Result afterSessionInfoChanged()
	{
		return gplat::Result().setOk();
	}

public:
	uint64_t sessionId() const
	{
		return *m_session_instant_id;
	}

public:
	uint16_t m_session_group{0};

public:
	uint64_t m_last_echo_send_tick{0};
	uint64_t m_last_echo_recv_tick{0};
	uint32_t m_echo_request_count{0}; ///< recv받는 순간에 클리어

	boost::weak_ptr<Server> m_server;

protected:
	string_t m_client_address;
	SocketBase *m_base_socket{nullptr};
	session_state_e::type m_session_state{session_state_e::session_init};

protected:
	boost::shared_ptr<InstantId> m_session_instant_id;

public:
	uint16_t m_channel_server_id{0};
	uint16_t m_logic_server_id{0};
	uint16_t m_zone_server_id{0};
	int32_t m_community_id{0};
	int32_t m_guild_db_id{0};
	int32_t m_user_db_id{0};

protected:
	SessionManager *m_session_manager{nullptr};
};
