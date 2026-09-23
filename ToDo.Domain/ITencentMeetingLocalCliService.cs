using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ToDo.Domain.Dto;

namespace ToDo.Domain;

public interface ITencentMeetingLocalCliService
{
    /// <summary>
    /// 调用tmeet CLI查询指定日期当日可拉取转写的会议列表
    /// </summary>
    /// <param name="meetDate">查询日期</param>
    /// <returns>当日会议列表</returns>
    Task<List<TencentMeetDailyItem>> QueryDailyMeetListAsync(DateTime meetDate);

    /// <summary>
    /// 根据recordFileId拉取单场会议清洗后的转写文本
    /// </summary>
    /// <param name="recordFileId">录制文件ID</param>
    /// <param name="title">会议标题（可选，如不传则使用recordFileId）</param>
    /// <returns>转写结果</returns>
    Task<TencentMeetTranscriptResult> FetchSingleMeetTranscriptAsync(string recordFileId, string? title = null);

    /// <summary>
    /// 批量拉取多场会议并自动合并内容
    /// </summary>
    /// <param name="recordFileIds">选中ID数组</param>
    /// <param name="titleMap">RecordFileId -> 标题的映射（可选）</param>
    /// <returns>合并后完整文本+所有原始JSON拼接</returns>
    Task<(string MergedCleanContent, string AllRawJson, string RecordIdsJoin)> BatchFetchAndMergeAsync(
        List<string> recordFileIds,
        Dictionary<string, string>? titleMap = null);
}
