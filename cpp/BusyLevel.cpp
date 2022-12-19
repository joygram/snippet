//

#include "preheader.h"

#include "BusyLevel.h"
#include <libGen/cpp/log/Logger.h>

#include <boost/chrono/chrono.hpp>
#include <boost/range/irange.hpp>
#include <algorithm>

BusyLevel::BusyLevel(decide_method_e::TYPE decideType /*= eDecideType::VALUE*/)
{
	setDefaultLoggerName("monitor.busylevel");

	const int32_t DEFAULT_BUSYLEVEL_DATA_MAX_COUNT = 20;
	m_current_busy_level = BusyLevel_e::BUSY_IDLE;
	m_sample_count = DEFAULT_BUSYLEVEL_DATA_MAX_COUNT;
	m_decide_method = decideType;
	m_useLog = true;
	m_log_interval = 60.0f;
	m_last_time_point = clock_t::now();

	int32_t count = static_cast<int16_t>(BusyLevel_e::_END);
	for (counting(value, (int32_t)(0), count))
	{
		m_busyValue[value] = 0.0f;
		m_toGoodValue[value] = 0.0f;
	}

	m_outlier = 0.f;

	m_use_session_action = false;
	m_use_fatal_action = false;
}

void BusyLevel::setup(const BusyLevelParam &busyLevelParam, const string_t &loggerName /*= ""*/)
{
	if (!loggerName.empty())
	{
		setDefaultLoggerName(loggerName);
	}

	setSampleCount(busyLevelParam.m_sampleCount);
	setDecideType(static_cast<decide_method_e::TYPE>(busyLevelParam.m_decideType));
	setLog(busyLevelParam.m_useLog, busyLevelParam.m_logInterval);

	setBusyValue(BusyLevel_e::BUSY_FATAL, busyLevelParam.m_busyFatal);
	setBusyValue(BusyLevel_e::BUSY_ERROR, busyLevelParam.m_busyError);
	setBusyValue(BusyLevel_e::BUSY_WARN, busyLevelParam.m_busyWarn);
	setBusyValue(BusyLevel_e::BUSY_IDLE, busyLevelParam.m_busyIdle);

	setToGoodValue(BusyLevel_e::BUSY_FATAL, busyLevelParam.m_busyFatalToIdle);
	setToGoodValue(BusyLevel_e::BUSY_ERROR, busyLevelParam.m_busyErrorToIdle);
	setToGoodValue(BusyLevel_e::BUSY_WARN, busyLevelParam.m_busyWarnToIdle);

	m_use_session_action = busyLevelParam.m_useSessionAction;
	m_use_fatal_action = busyLevelParam.m_useFatalAction;
}

bool BusyLevel::decide(float aValue, const string_t &callerName, uint16_t sliceCount /*= 0*/)
{
	if (!isValid())
	{
		// DIRECT_LOG_INFO(sformat("CALLER:{0} maxBusyLevel Value shuold set. can not decice busy level.", callerName));
		return false;
	}

	float totalValue = 0;
	float averageValue = 0;
	{
		spin_mutex_t::scoped_lock lock(m_datas_mutex);
		m_datas.push_back(aValue);

		int32_t dataListSize = static_cast<int32_t>(m_datas.size());
		if (dataListSize == m_sample_count + 1)
		{
			m_datas.pop_front();
		}

		int32_t maxDataCount = dataListSize > m_sample_count ? dataListSize : m_sample_count; ///< 데이터가 다 차지 않았을 경우
		for (auto &value : m_datas)
		{
			totalValue += value;
		}

		m_recent_average_value = averageValue = totalValue / m_sample_count;
	}

	if (m_useLog)
	{
		timePoint_t start_time_point = clock_t::now();
		timePoint_t::duration diff = start_time_point - m_last_time_point;
		float duration = static_cast<float>(diff.count());

		if (duration >= m_log_interval)
		{
			spin_mutex_t::scoped_lock lock(m_outlier_mutex);

			// NAMED_INFO("monitor.busyvalue", sformat("{0},{1},SliceCount:{2}", ffdot(averageValue, 0, 5), m_outlier, sliceCount));
			// NAMED_INFO("monitor.busyvalue", sformat("{0},{1},SliceCount:{2}", averageValue, m_outlier, sliceCount));
			NAMED_INFO("monitor.busyvalue", "{0},{1},SliceCount:{2}", averageValue, m_outlier, sliceCount);
			m_outlier = 0.f;

			m_last_time_point = start_time_point;
		}
	}

	/// busy level 구간 : 100 < - very busy  - > 95 < - most busy - > 80 < - busy - > 70 < - good - > 0
	/// 복구 구간 : very busy -10%-> good, most busy -30%-> good, busy -50%-> good
	float busyFatal = m_busyValue[BusyLevel_e::BUSY_FATAL];
	float busyError = 0.0f;
	float busyWarn = 0.0f;
	float busyIdle = 0.0f;

	float busyFatalToIdle = 0.0f;
	float busyErrorToIdle = 0.0f;
	float busyWarnToIdle = 0.0f;

	if (decide_method_e::PERCENT == m_decide_method)
	{
		busyError = busyFatal * m_busyValue[BusyLevel_e::BUSY_ERROR];
		busyWarn = busyFatal * m_busyValue[BusyLevel_e::BUSY_WARN];
		busyIdle = busyFatal * m_busyValue[BusyLevel_e::BUSY_IDLE];

		busyFatalToIdle = busyFatal * m_toGoodValue[BusyLevel_e::BUSY_FATAL]; //이하로 떨어지면 정상복구 처리
		busyErrorToIdle = busyFatal * m_toGoodValue[BusyLevel_e::BUSY_ERROR];
		busyWarnToIdle = busyFatal * m_toGoodValue[BusyLevel_e::BUSY_WARN];
	}
	else
	{
		busyError = m_busyValue[BusyLevel_e::BUSY_ERROR];
		busyWarn = m_busyValue[BusyLevel_e::BUSY_WARN];
		busyIdle = m_busyValue[BusyLevel_e::BUSY_IDLE];

		busyFatalToIdle = m_toGoodValue[BusyLevel_e::BUSY_FATAL]; //이하로 떨어지면 정상복구 처리
		busyErrorToIdle = m_toGoodValue[BusyLevel_e::BUSY_ERROR];
		busyWarnToIdle = m_toGoodValue[BusyLevel_e::BUSY_WARN];
	}

	BusyLevel_e::TYPE backupBusyLevel = m_current_busy_level;

	switch (currentBusyLevel())
	{
	case BusyLevel_e::BUSY_IDLE:
	{
		if (averageValue > busyError) ///
		{
			m_current_busy_level = BusyLevel_e::BUSY_FATAL;
		}
		else if (averageValue > busyWarn && averageValue <= busyError)
		{
			m_current_busy_level = BusyLevel_e::BUSY_ERROR;
		}
		else if (averageValue > busyIdle && averageValue <= busyWarn)
		{
			m_current_busy_level = BusyLevel_e::BUSY_WARN;
		}
	}
	break;

	case BusyLevel_e::BUSY_WARN:
	{
		if (averageValue > busyError) ///
		{
			m_current_busy_level = BusyLevel_e::BUSY_FATAL;
		}
		else if (averageValue > busyWarn && averageValue <= busyError)
		{
			m_current_busy_level = BusyLevel_e::BUSY_ERROR;
		}
		else if (averageValue <= busyWarnToIdle) /// 정상복귀
		{
			m_current_busy_level = BusyLevel_e::BUSY_IDLE;
		}
	}
	break;

	case BusyLevel_e::BUSY_ERROR:
	{
		if (averageValue > busyError) /// 더 바뻐졌어 ㅠ..ㅠ
		{
			m_current_busy_level = BusyLevel_e::BUSY_FATAL;
		}
		else if (averageValue <= busyErrorToIdle) ///정상복귀
		{
			m_current_busy_level = BusyLevel_e::BUSY_IDLE;
		}
	}
	break;

	case BusyLevel_e::BUSY_FATAL:
	{
		if (averageValue <= busyFatalToIdle) ///정상복귀
		{
			m_current_busy_level = BusyLevel_e::BUSY_IDLE;
		}
	}
	break;

	default:
		break;
	} // switch

	// 처음의 상태값과 연산 이 후의 상태가 다르다면 변화가 있었으므로 true를 리턴해준다.
	return (backupBusyLevel != m_current_busy_level);
}

void BusyLevel::setOutlier(float aValue)
{
	spin_mutex_t::scoped_lock lock(m_outlier_mutex);
	m_outlier = std::max(m_outlier, aValue);
}
