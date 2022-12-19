using log4net;

using System;
using System.Collections.Generic;

namespace gplat
{
	//requester 를 타입으로 받고 해당 타입의 인터페이스의 명령을 수행한다. 
	//requester에 IRequester를 추가 
	public interface IPacketSchedule
	{
		void setScheduled(bool use_scheduler);
		gplat.Result onScheduledProcess();
		void resetSchedule();
	}

	public class PacketScheduleProcessor
		: gplat.ScheduledRequester
	{
		protected IPacketSchedule m_schedule = null; //packet requester 
		public gplat.Result schedule(IPacketSchedule schedule, Int32 interval, Int32 count)
		{
			m_schedule = schedule;
			m_interval = interval;
			//서버와 코드 동기화
			setInterval(m_interval);

			m_schedule.setScheduled(true);

			//requster 재시도 횟수 조정, timeout조정 : 짧고 빠르게 
			var my_type = schedule.GetType();
			gplat.ScheduledProcessorManager.it.register(my_type.Name, this);
			return gplat.Result.alloc().setOk();
		}
		protected override ScheduledRequester create()
		{
			return new PacketScheduleProcessor();
		}

		public override Result onProcess()
		{
			return m_schedule.onScheduledProcess();
		}
		public override void onCleanup()
		{
			m_schedule.resetSchedule();

			if (m_gen_result.exceptionOccurred())
			{
				gplat.Log.logger("exception").Fatal($"EXCEPTION:{m_gen_result}");
			}
			else if (m_gen_result.fail())
			{
				//todo result code 
				m_log.Error($"{m_gen_result}");
			}

			m_log.Debug($"Scheduled Job Completed");
		}

		public override void Dispose()
		{

		}
	}
	public class PacketScheduler<SCHEDULER_TYPE> where SCHEDULER_TYPE : new()
	{
		protected List<string> m_scheduler_names = new List<string>();

		public static SCHEDULER_TYPE it = new SCHEDULER_TYPE();
		protected ILog m_log = gplat.Log.logger("packetscheduler");
		public virtual void start()
		{
			m_log.Debug("--START SCHEDULE--");
			onSetupSchedule();
			gplat.ScheduledProcessorManager.it.activateAll();
		}
		public virtual void stop()
		{
			m_log.Debug("--STOPPED--");
			gplat.ScheduledProcessorManager.it.cancelAll();
			gplat.ScheduledProcessorManager.it.managerLock();
		}
		public virtual void onSetupSchedule() { }
	}
}
