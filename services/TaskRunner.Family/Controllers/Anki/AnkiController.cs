using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TaskRunner.Helpers;
using TaskRunner.Contracts.Anki;

namespace TaskRunner.Controllers;
    /// <summary>
    /// Anki 卡片生成控制器
    /// </summary>
    [ApiController]
    [Route("api/anki")]
    public partial class AnkiController : ControllerBase
    {
        private readonly Services.AnkiCardGenerator _cardGenerator;
        private readonly Services.DailyCardService _dailyCardService;
        private readonly Services.AchievementEngine _achievementEngine;
        private readonly Services.LearnerService _learnerService;
        private readonly Services.VaultSettingsService _vaultSettings;
        private readonly Services.TaskManager _taskManager;
        private readonly ILogger<AnkiController> _logger;

        public AnkiController(
            Services.AnkiCardGenerator cardGenerator,
            Services.DailyCardService dailyCardService,
            Services.AchievementEngine achievementEngine,
            Services.LearnerService learnerService,
            Services.VaultSettingsService vaultSettings,
            Services.TaskManager taskManager,
            ILogger<AnkiController> logger)
        {
            _cardGenerator = cardGenerator;
            _dailyCardService = dailyCardService;
            _achievementEngine = achievementEngine;
            _learnerService = learnerService;
            _vaultSettings = vaultSettings;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>
        /// 从单个笔记生成 Anki 卡片
        /// </summary>
        [HttpPost("generate")]
        public async Task<ActionResult<GenerateResult>> GenerateFromNote([FromBody] GenerateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NotePath))
            {
                return BadRequest(new GenerateResult { Success = false, Message = "笔记路径不能为空" });
            }

            var result = await _cardGenerator.GenerateFromNote(request.NotePath);
            return Ok(result);
        }

        /// <summary>
        /// 批量生成卡片（从目录）
        /// </summary>
        [HttpPost("generate-batch")]
        public async Task<ActionResult<BatchGenerateResult>> GenerateFromDirectory([FromBody] BatchGenerateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Directory))
            {
                return BadRequest(new BatchGenerateResult { Success = false, Message = "目录不能为空" });
            }

            var result = await _cardGenerator.GenerateFromDirectory(request.Directory, request.Recursive);
            return Ok(result);
        }
}
