//
#pragma once

#include <libGen/cpp/base/BusyLevel.h>
#include <msg_gen_manage_types.h>
class Session;

// 게임서버 할당 기준
// - 혼잡도가 가장 낮은 녀석
// - 최소인원 미만인 녀석들 중 가장 큰 녀석
// - 최소인원 이상인 녀석들 중 가장 작은 녀석
template <typename BALANCE_OBJECT>
class ServerBalancer
	: public LoggerBaseInfo
{
public:
	ServerBalancer()
	{
		setDefaultLoggerName("balancer.gameserver");
	}

public:
	void changeServerBusyLevel(uint16_t server_id, BusyLevel_e::TYPE busy_level)
	{
		boost::shared_ptr<BALANCE_OBJECT> balance_object;
		if (findObject(server_id, balance_object))
		{
			balance_object->m_busy_level = busy_level;
		}
	}

	// 모두가 특정 상태 이하이면
	bool allBusyLevelUnder(BusyLevel_e::TYPE busy_level)
	{
		for (boost::shared_ptr<BALANCE_OBJECT> info : m_balance_objects)
		{
			if (info->m_busy_level > busy_level) ///하나라도 크면
			{
				return false;
			}
		}
		return true;
	}

public:
	gplat::Result registerServer(boost::shared_ptr<BALANCE_OBJECT> balance_object)
	{
		gplat::Result gen_result;
		boost::shared_ptr<BALANCE_OBJECT> exist_balance_object;
		if (findObject(balance_object->balanceKeyServerId(), exist_balance_object))
		{
			return gen_result.setFail(sformat("server_id:{0} already exist", balance_object->balanceKeyServerId()));
		}
		balance_object->setBusyLevel(BusyLevel_e::BUSY_IDLE);
		m_balance_objects.push_back(balance_object);
		LOG_TRACE("registered: balanceKeyServerId:{0}", balance_object->balanceKeyServerId());

		return gen_result.setOk();
	}

	void unregisterServer(int32_t server_id)
	{
		LOG_TRACE("try unregister: balanceKeyServerId:{0}", server_id);

		auto it = std::remove_if(m_balance_objects.begin(), m_balance_objects.end(),
								 [server_id](boost::shared_ptr<BALANCE_OBJECT> balance_object) -> bool
								 {
									 return (balance_object->balanceKeyServerId() == server_id);
								 });

		if (it != m_balance_objects.end())
		{
			m_balance_objects.erase(it);
		}
		m_dedicated_object.reset(); //무조건 리셋
	}

	int32_t serverCount()
	{
		return static_cast<int32_t>(m_balance_objects.size());
	}

public:
	boost::shared_ptr<BALANCE_OBJECT> alloc()
	{
		auto base_fill_user_count = m_base_fill_user_count;
		// 기준선 미만으로 먼저 정렬을 해서 원하는게 없을 경우 기준선 이상으로 정렬을 수행한다.
		boost::shared_ptr<BALANCE_OBJECT> balance_object;

		// dedicated_object // base_fill_user_count보다 높으면 통과
		if (m_dedicated_object && m_dedicated_object->balanceKeyUserCount() < base_fill_user_count)
		{
			return m_dedicated_object;
		}

		std::sort(m_balance_objects.begin(), m_balance_objects.end(),
				  [&](boost::shared_ptr<BALANCE_OBJECT> left, boost::shared_ptr<BALANCE_OBJECT> right) -> bool
				  {
					  if (left->busyLevel() > right->busyLevel()) /// 상태값이 큰녀석을 앞으로 if first is equal then second key
					  {
						  return true;
					  }
					  else if (left->busyLevel() < right->busyLevel())
					  {
						  return false;
					  }
					  // file_user_count가 미만인 경우가 우선
					  else if (left->balanceKeyUserCount() > right->balanceKeyUserCount()) // currentCount가 큰 녀석을 앞으로
					  {
						  return true;
					  }
					  else if (left->balanceKeyUserCount() < right->balanceKeyUserCount())
					  {
						  return false;
					  }
					  else if (left->balanceKeyServerId() < right->balanceKeyServerId()) // server_id()가 작은 녀석을 앞으로
					  {
						  return true;
					  }
					  return false;
				  });

		// 혼잡도가 높거나 기준값 이상으로 채워져있는 경우 조건에 만족하는 것이 없으므로 기준값 이상의 값을 찾도록 한다.
		bool not_exist = false;
		balance_object = m_balance_objects.front();

		if (!balance_object)
		{
			LOG_ERROR("object not exist. m_balance_objects empty.");
			return boost::shared_ptr<BALANCE_OBJECT>();
		}

		if (balance_object->busyLevel() < BusyLevel_e::BUSY_WARN || balance_object->balanceKeyUserCount() >= m_base_fill_user_count)
		{
			not_exist = true;
		}

		if (not_exist)
		{
			std::sort(m_balance_objects.begin(), m_balance_objects.end(),
					  [&](boost::shared_ptr<BALANCE_OBJECT> left, boost::shared_ptr<BALANCE_OBJECT> right) -> bool
					  {
						  if (left->busyLevel() > right->busyLevel()) /// 상태값이 큰녀석을 앞으로 if first is equal then second key
						  {
							  return true;
						  }
						  else if (left->busyLevel() < right->busyLevel())
						  {
							  return false;
						  }
						  // file_user_count가 이상 인 경우가 우선
						  else if (left->balanceKeyUserCount() < right->balanceKeyUserCount()) // currentCount가 작은 녀석을 앞으로
						  {
							  return true;
						  }
						  else if (left->balanceKeyUserCount() > right->balanceKeyUserCount())
						  {
							  return false;
						  }
						  else if (left->balanceKeyServerId() < right->balanceKeyServerId()) // server_id()가 작은 녀석을 앞으로
						  {
							  return true;
						  }
						  return false;
					  });
		}

		// 맨앞녀석이 NORMAL이 아니면 쓸수 있는 건 없음.
		balance_object = m_balance_objects.front();
		if (balance_object->busyLevel() < BusyLevel_e::BUSY_WARN)
		{
			LOG_TRACE("no idle gameserver");
			return boost::shared_ptr<BALANCE_OBJECT>();
		}

		m_dedicated_object = balance_object;

		return balance_object;
	}

	bool findObject(int32_t server_id, _out boost::shared_ptr<BALANCE_OBJECT> &balance_object)
	{
		auto it = std::find_if(m_balance_objects.begin(), m_balance_objects.end(),
							   [server_id](boost::shared_ptr<BALANCE_OBJECT> info) -> bool
							   {
								   return (info->balanceKeyServerId() == server_id);
							   });

		if (it == m_balance_objects.end())
		{
			return false;
		}
		balance_object = (*it);
		return true;
	}

	string_t toString()
	{
		string_t output;
		for (auto balance_object : m_balance_objects)
		{
			output += balance_object->toString() + " "; // +"\n";
		}
		return output;
	}

	std::vector<boost::shared_ptr<BALANCE_OBJECT>> balanceObjects()
	{
		return m_balance_objects;
	}

public:
	int32_t m_base_fill_user_count{200};

private:
	std::vector<boost::shared_ptr<BALANCE_OBJECT>> m_balance_objects;
	boost::shared_ptr<BALANCE_OBJECT> m_dedicated_object;
};
