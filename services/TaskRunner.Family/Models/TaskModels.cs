using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TaskRunner.Models
{
    /// <summary>
    /// 任务状态枚举
    /// </summary>
    public enum TaskStatus
    {
        /// <summary>
        /// 等待中
        /// </summary>
        Pending = 0,

        /// <summary>
        /// 执行中
        /// </summary>
        Running = 1,

        /// <summary>
        /// 成功
        /// </summary>
        Success = 2,

        /// <summary>
        /// 失败
        /// </summary>
        Failed = 3,

        /// <summary>
        /// 超时
        /// </summary>
        Timeout = 4,

        /// <summary>
        /// 已取消
        /// </summary>
        Cancelled = 5
    }

    /// <summary>
    /// 任务进度
    /// </summary>
    public class TaskProgress
    {
        /// <summary>
        /// 当前步骤
        /// </summary>
        public int Current { get; set; }

        /// <summary>
        /// 总步骤数
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// 进度消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 完成百分比
        /// </summary>
        public double Percentage { get; set; }
        
        /// <summary>
        /// 构造函数，自动计算百分比
        /// </summary>
        public TaskProgress()
        {
            Current = 0;
            Total = 1;
            Message = string.Empty;
            Percentage = 0.0;
        }
        
        /// <summary>
        /// 更新进度并自动计算百分比
        /// </summary>
        public void Update(int current, int total, string message)
        {
            Current = current;
            Total = total;
            Message = message;
            Percentage = total > 0 ? (double)current / total * 100 : 0.0;
        }
    }

    /// <summary>
    /// 任务结果
    /// </summary>
    public class TaskResult
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 结果数据
        /// </summary>
        public object? Data { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// 任务信息
    /// </summary>
    public class TaskInfo
    {
        /// <summary>
        /// 任务 ID
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 任务类型
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// 任务状态
        /// </summary>
        public TaskStatus Status { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// 任务进度
        /// </summary>
        public TaskProgress Progress { get; set; } = new();

        /// <summary>
        /// 任务结果
        /// </summary>
        public TaskResult? Result { get; set; }
    }

    /// <summary>
    /// 任务响应
    /// </summary>
    public class TaskResponse
    {
        /// <summary>
        /// 任务 ID
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// 任务状态
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// 消息
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 拆分测试请求
    /// </summary>
    public class SplitTestRequest
    {
        public string Title { get; set; } = string.Empty;
        public string SourceContent { get; set; } = string.Empty;
    }
}
