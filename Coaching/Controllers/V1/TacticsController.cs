using Asp.Versioning;
using Coaching.Application.DTOs.Tactics;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class TacticsController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly ITacticsBoardService _tactics;

    public TacticsController(ITacticsBoardService tactics, IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _tactics = tactics;
    }

    [HttpGet("tactics-boards")]
    public async Task<IActionResult> GetBoards([FromQuery] TacticsShelfQuery query)
    {
        CheckIsUserLoggedIn();
        return Ok(await _tactics.ListBoardsAsync(query, JwtPayload!.UserId));
    }

    [HttpGet("tactics-boards/{id:guid}")]
    public async Task<IActionResult> GetBoard([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        return Ok(await _tactics.GetBoardAsync(id, JwtPayload!.UserId));
    }

    /// <summary>
    /// Creates or replaces one board at an id the client chose. The editor saves whatever it is
    /// holding whenever it settles, so the same save arriving twice must leave one board, not two.
    /// </summary>
    [HttpPut("tactics-boards/{id:guid}")]
    public async Task<IActionResult> SaveBoard([FromRoute] Guid id, [FromBody] SaveTacticsBoardRequest request)
    {
        CheckIsUserLoggedIn();
        return Ok(await _tactics.SaveBoardAsync(id, request, JwtPayload!.UserId));
    }

    /// <summary>Fills an empty shelf with the starter boards, in one write and only once.</summary>
    [HttpPost("tactics-boards/seed")]
    public async Task<IActionResult> SeedBoards([FromBody] SeedTacticsBoardsRequest request)
    {
        CheckIsUserLoggedIn();
        return Ok(await _tactics.SeedBoardsAsync(request, JwtPayload!.UserId));
    }

    [HttpDelete("tactics-boards/{id:guid}")]
    public async Task<IActionResult> DeleteBoard([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        await _tactics.DeleteBoardAsync(id, JwtPayload!.UserId);
        return NoContent();
    }

    [HttpGet("tactics-folders")]
    public async Task<IActionResult> GetFolders([FromQuery] TacticsShelfQuery query)
    {
        CheckIsUserLoggedIn();
        return Ok(await _tactics.ListFoldersAsync(query, JwtPayload!.UserId));
    }

    [HttpPost("tactics-folders")]
    public async Task<IActionResult> CreateFolder([FromBody] CreateTacticsFolderRequest request)
    {
        CheckIsUserLoggedIn();
        var folder = await _tactics.CreateFolderAsync(request, JwtPayload!.UserId);
        return CreatedAtAction(nameof(GetFolders), new { version = "1.0" }, folder);
    }

    [HttpPut("tactics-folders/{id:guid}")]
    public async Task<IActionResult> RenameFolder([FromRoute] Guid id, [FromBody] RenameTacticsFolderRequest request)
    {
        CheckIsUserLoggedIn();
        return Ok(await _tactics.RenameFolderAsync(id, request.Name, JwtPayload!.UserId));
    }

    [HttpDelete("tactics-folders/{id:guid}")]
    public async Task<IActionResult> DeleteFolder([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        await _tactics.DeleteFolderAsync(id, JwtPayload!.UserId);
        return NoContent();
    }
}
