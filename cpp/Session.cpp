//
#include "preheader.h"
#include "Session.h"
#include <libGen/cpp/network/SocketBase.h>

#include "SessionManager.h"
#include "Server.h"
#include <boost/enable_shared_from_this.hpp>

using boost::asio::ip::tcp;

#if BOOST_VERSION >= 107000
static gplat::asio::io_context null_io_context;

Session::Session(void)
	: Socket(null_io_context, "")
{
}

#else
Session::Session(void)
	: Socket(boost::shared_ptr<boost::asio::ip::tcp::socket>(), "")
{
}
#endif // BOOST_VERSION

Session::~Session(void)
{
	LOG_DEBUG("Session close session id : {0}", sessionId());
}

void Session::setSocket(SocketBase *socket)
{
	m_base_socket = socket;
}

void Session::onClose()
{
	LOG_DEBUG("closed sessionId:{0}", *m_session_instant_id);
	setSessionState(session_state_e::session_closed);

	msg_gen_network::notify_socket_closed notify;
	notify.session_id = sessionId();
	notify.closeReason;
	notifyNetMsg(notify);

	if (m_session_manager)
	{
		m_session_manager->removeSession(shared_from_this());
	}
}

void Session::afterAddToManager()
{
	setSockState(socket_state_e::CONNECTED);

	startSocket(sessionId());
	setPrivateIp();
	setRemoteIp();

	LOG_TRACE("complete session add to manager");
}

gplat::Result Session::init(SessionManager *session_manager)
{
	if (NULL == m_base_socket)
	{
		LOG_ERROR("m_baseSocket is not set!");
		return false;
	}
	if (m_base_socket->asioSocket())
	{
		boost::system::error_code boost_error_code;
		tcp::endpoint client_end_point = m_base_socket->asioSocket()->remote_endpoint(boost_error_code);

		if (0 == boost_error_code.value())
		{
			string_t ip_address = client_end_point.address().to_string(boost_error_code);
			if (0 == boost_error_code.value() && false == ip_address.empty())
			{
				m_client_address = ip_address;
			}
		}
		setSessionState(session_state_e::session_established);
		gplat::Result res = m_base_socket->setSocketOptions();
		if (res.fail())
		{
			return res;
		}
	}
	m_session_manager = session_manager;
	return afterInitSession();
}

gplat::Result Session::afterInitSession()
{
	return gplat::Result().setOk();
}
