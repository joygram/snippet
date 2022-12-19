//
//

#pragma once

#include "Concurrency.h"
#include <boost/chrono.hpp>
#include <libGen/cpp/log/LoggerBaseInfo.h>

/**
busy level 처리방식
- BUSY_FATAL:월드-입장제한, 관련 사용자 접속 종료
- BUSY_ERROR:월드-입장제한
- BUSY_WARN :월드-포화, 대기표발행
- BUSY_IDLE :월드-동접인원으로 제어
*/
struct BusyLevel_e
{
	enum TYPE
	{
		_BEGIN,
		SHUTDOWN, // 전체-월드-점검중, 손님 안받음, 서비스 불가

		INTERNAL_LEVEL0, // 손님용아님, 서비스중 
		LEVEL3, //전체-월드-점검중, 모두 내보냄, 서비스 안함, 
		LEVEL2, //전체-월드-입장제한, 손님 안받음, 서비스중 
		LEVEL1, //전체-월드-포화, 대기표 손님 받음, 서비스중, 80~100% 
		LEVEL0, //전체-월드-원할, 손님 받음, 서비스 중

		INTERNAL_BUSY_IDLE = INTERNAL_LEVEL0,
		BUSY_FATAL = LEVEL3,
		BUSY_ERROR = LEVEL2,
		BUSY_WARN = LEVEL1,
		BUSY_IDLE = LEVEL0,
		_END
	};

	static string_t ToString(TYPE type)
	{
		switch (type)
		{
		case SHUTDOWN: return "SHUTDOWN";
		case INTERNAL_BUSY_IDLE: return "INTERNAL_BUSY_IDLE";
		case BUSY_FATAL: return "BUSY_FATAL";
		case BUSY_ERROR: return "BUSY_ERROR";
		case BUSY_WARN: return "BUSY_WARN";
		case BUSY_IDLE: return "BUSY_IDLE";
		case _END: return "EOE";
		}

		return "BusyLevel_e::UNKNOWN";
	}
};


/// busy-level 파라메터로 전달하여 세팅
struct BusyLevelParam
{
	BusyLevelParam()
	{
		m_decideType = 0;
		m_sampleCount = 0;
		m_useLog = true;
		m_logInterval = 60.0f;

		m_busyFatal = 0.0f;
		m_busyError = 0.0f;
		m_busyWarn = 0.0f;
		m_busyIdle = 0.0f;

		m_busyFatalToIdle = 0.0f;
		m_busyErrorToIdle = 0.0f;
		m_busyWarnToIdle = 0.0f;

		m_useSessionAction = false;
		m_useFatalAction = false;
	}

	uchar_t m_decideType;
	int32_t m_sampleCount;
	bool m_useLog;
	float m_logInterval;

	float m_busyFatal;
	float m_busyError;
	float m_busyWarn;
	float m_busyIdle;

	float m_busyFatalToIdle;
	float m_busyErrorToIdle;
	float m_busyWarnToIdle;

	bool m_useSessionAction;
	bool m_useFatalAction;
};


/**
busy level을 구간별로 결정하는 기능수행, 평균값 계산을 위해 데이터를 리스트로 유지한다.
*/
class BusyLevel
	: public LoggerBaseInfo
{
public:
	struct decide_method_e
	{
		enum TYPE
		{
			VALUE, // 값 방식 구간 비교 
			PERCENT // percent방식 구간비교 
		};
	};

	typedef boost::chrono::high_resolution_clock clock_t;
	typedef boost::chrono::time_point<clock_t, boost::chrono::duration<double>> timePoint_t;

public:
	BusyLevel(decide_method_e::TYPE decideType = decide_method_e::VALUE);

public:
	void setup(const BusyLevelParam& busyLevelParam, const string_t& loggerName = "");

	void setDecideType(decide_method_e::TYPE decide_method)
	{
		m_decide_method = decide_method;
	}

	void setLog(bool use_log, float log_interval)
	{
		m_useLog = use_log;
		m_log_interval = log_interval;
	}

	void setSampleCount(int32_t sample_count)
	{
		m_sample_count = sample_count;
	}

	/// busylevel에서 good으로 돌아오기 위한 값

	void setToGoodValue(BusyLevel_e::TYPE busylevel, float aValue)
	{
		m_toGoodValue[busylevel] = aValue;
	}

	void setBusyValue(BusyLevel_e::TYPE busylevel, float aValue)
	{
		m_busyValue[busylevel] = aValue;
	}

	/// FATAL은 반드시 제공되어야 하므로 없는 경우 세팅이 안되었다고 볼 수 있다. 

	bool isValid() const
	{
		return (m_busyValue[BusyLevel_e::BUSY_FATAL] != 0.0f);
	}

public:
	bool decide(float aValue, const string_t& callerName, uint16_t sliceCount = 0);

	BusyLevel_e::TYPE currentBusyLevel() const
	{
		return m_current_busy_level;
	}

	float recentAverageValue() const
	{
		return m_recent_average_value;
	}

	int32_t sampleCount() const
	{
		return m_sample_count;
	}

	bool useSessionAction() const
	{
		return m_use_session_action;
	}

	bool useFatalAction() const
	{
		return m_use_fatal_action;
	}

	void setOutlier(float aValue);


private:
	int32_t m_sample_count;

	std::list<float> m_datas;
	spin_mutex_t m_datas_mutex;
	spin_mutex_t m_outlier_mutex;

	float m_outlier;
	float m_recent_average_value;	// 데이터 점검을 위해서 최근 비지 레벨 검사에서 사용된 평균값을 저장한다.
									// 실제로 이 데이터가 검사에 사용되거나 하지는 않는다.
	BusyLevel_e::TYPE m_current_busy_level;
	decide_method_e::TYPE m_decide_method;
	bool m_useLog;
	float m_log_interval;
	timePoint_t m_last_time_point;

	float m_busyValue[BusyLevel_e::_END];
	float m_toGoodValue[BusyLevel_e::_END];

	bool m_use_session_action;
	bool m_use_fatal_action;
};

