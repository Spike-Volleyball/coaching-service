using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.RichText;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Templates;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class DrillDialService : IDrillDialService
{
    private readonly IDrillRepository _drillRepository;
    private readonly IRepository<DrillDial> _dialRepository;
    private readonly IRepository<PlanItemDialValue> _valueRepository;
    private readonly IDrillDialReconciler _reconciler;
    private readonly IRepository<DrillVariation> _variationRepository;
    private readonly IRepository<ImprovementPointDrill> _pointDrillRepository;
    private readonly IClubsGrpcClient _clubsClient;
    private readonly IDrillService _drillService;

    public DrillDialService(
        IDrillRepository drillRepository,
        IRepository<DrillDial> dialRepository,
        IRepository<PlanItemDialValue> valueRepository,
        IRepository<DrillVariation> variationRepository,
        IRepository<ImprovementPointDrill> pointDrillRepository,
        IClubsGrpcClient clubsClient,
        IDrillService drillService,
        IDrillDialReconciler reconciler)
    {
        _drillRepository = drillRepository;
        _dialRepository = dialRepository;
        _valueRepository = valueRepository;
        _variationRepository = variationRepository;
        _pointDrillRepository = pointDrillRepository;
        _clubsClient = clubsClient;
        _drillService = drillService;
        _reconciler = reconciler;
    }

    public async Task<DrillDto> AddAsync(Guid drillId, CreateDrillDialDto request, Guid userId)
    {
        var drill = await LoadForEditAsync(drillId, userId);

        var name = request.Name?.Trim() ?? string.Empty;
        _reconciler.EnsureValidName(name);

        if (drill.Dials.Any(d => d.Name == name))
            throw new BadRequestException($"This drill already has a dial called {name}", ErrorCodeEnum.ValidationError);

        var dial = new DrillDial
        {
            DrillId = drill.Id,
            Name = name,
            Kind = request.Kind,
            Order = drill.Dials.Count == 0 ? 0 : drill.Dials.Max(d => d.Order) + 1,
        };
        _reconciler.ApplyKindFields(dial, request.Kind, request.DefaultValue, request.OnText, request.OffText, request.OnLabel, request.OffLabel);

        WriteInstructions(drill, request.InstructionsHtml, drill.Dials.Select(d => d.Name).Append(name));

        _dialRepository.Add(dial);

        // Every plan already using this drill gets the default, so the coach opens an existing
        // plan and finds the new dial set rather than blank.
        var uses = await _reconciler.LoadUsesAsync(drill.Id);
        var already = await _reconciler.ValuesForUsesAsync(uses);
        foreach (var use in uses.Where(u => !already.Any(v => _reconciler.Belongs(v, u) && v.DialName == name)))
            _valueRepository.Add(_reconciler.NewValue(use, name, dial.DefaultValue));

        await _drillRepository.SaveChangesAsync();
        return await ReadAsync(drillId, userId);
    }

    public async Task<DrillDto> UpdateAsync(Guid drillId, string name, UpdateDrillDialDto request, Guid userId)
    {
        var drill = await LoadForEditAsync(drillId, userId);
        var dial = FindDial(drill, name);

        var newName = request.NewName?.Trim();
        var renaming = !string.IsNullOrEmpty(newName) && newName != dial.Name;
        if (renaming)
        {
            _reconciler.EnsureValidName(newName!);
            if (drill.Dials.Any(d => d.Id != dial.Id && d.Name == newName))
                throw new BadRequestException($"This drill already has a dial called {newName}", ErrorCodeEnum.ValidationError);

            if (request.InstructionsHtml is null)
                throw new BadRequestException("A rename has to bring the re-tokenized instructions with it", ErrorCodeEnum.ValidationError);
        }

        _reconciler.ApplyKindFields(
            dial,
            dial.Kind,
            request.DefaultValue ?? dial.DefaultValue,
            request.OnText ?? dial.OnText,
            request.OffText ?? dial.OffText,
            request.OnLabel ?? dial.OnLabel,
            request.OffLabel ?? dial.OffLabel);

        if (request.InstructionsHtml is not null)
        {
            var names = drill.Dials.Select(d => d.Id == dial.Id && renaming ? newName! : d.Name);
            WriteInstructions(drill, request.InstructionsHtml, names);
        }

        if (renaming)
        {
            await RenameValuesAsync(drill.Id, dial.Name, newName!);
            dial.Name = newName!;
        }

        await _drillRepository.SaveChangesAsync();
        return await ReadAsync(drillId, userId);
    }

    public async Task<DrillDto> DeleteAsync(Guid drillId, string name, DeleteDrillDialDto request, Guid userId)
    {
        var drill = await LoadForEditAsync(drillId, userId);
        var dial = FindDial(drill, name);

        WriteInstructions(drill, request.InstructionsHtml, drill.Dials.Where(d => d.Id != dial.Id).Select(d => d.Name));

        var uses = await _reconciler.LoadUsesAsync(drill.Id);
        foreach (var value in (await _reconciler.ValuesForUsesAsync(uses)).Where(v => v.DialName == dial.Name))
            _valueRepository.Delete(value);

        _dialRepository.Delete(dial);

        await _drillRepository.SaveChangesAsync();
        return await ReadAsync(drillId, userId);
    }

    public async Task<FoldDrillResultDto> FoldAsync(Guid keepDrillId, FoldDrillDto request, Guid userId)
    {
        if (keepDrillId == request.SourceDrillId)
            throw new BadRequestException("A drill cannot be folded into itself", ErrorCodeEnum.ValidationError);

        // Both drills change, so both have to be the caller's to change.
        var keep = await LoadForEditAsync(keepDrillId, userId);
        var source = await LoadForEditAsync(request.SourceDrillId, userId);

        var supplied = request.ValuesForSourceUses ?? [];
        foreach (var (dialName, value) in supplied)
        {
            _reconciler.EnsureValidName(dialName);
            _reconciler.EnsureValueFits(value);
        }

        var (spine, grouped) = await _reconciler.LoadUseEntitiesAsync(source.Id);
        var uses = _reconciler.AsUses(spine, grouped).ToList();
        var existing = await _reconciler.ValuesForUsesAsync(uses);

        foreach (var item in spine) item.DrillId = keep.Id;
        foreach (var row in grouped) row.DrillId = keep.Id;

        // A use that already answered for one of the keeper's dials keeps its own answer; the
        // supplied values are for the ones it has never been asked.
        foreach (var use in uses)
            foreach (var (dialName, value) in supplied)
                if (!existing.Any(v => _reconciler.Belongs(v, use) && v.DialName == dialName))
                    _valueRepository.Add(_reconciler.NewValue(use, dialName, value));

        await RepointBlockingReferencesAsync(source.Id, keep.Id);

        // The same hard delete the drill editor does; the uses have already moved off it, so
        // nothing is left pointing here for the database to refuse.
        _drillRepository.Delete(source);

        await _drillRepository.SaveChangesAsync();
        return new FoldDrillResultDto(uses.Count);
    }

    /// <summary>
    /// Deleting a drill is refused while feedback or another drill's variation list still names
    /// it. A fold is a merge, so those move to the keeper rather than being dropped — a coach's
    /// improvement point should survive the two drills becoming one.
    /// </summary>
    private async Task RepointBlockingReferencesAsync(Guid sourceId, Guid keepId)
    {
        var variations = await _variationRepository.Query()
            .Where(v => v.TargetDrillId == sourceId)
            .ToListAsync();

        var keepersOwn = await _variationRepository.Query()
            .Where(v => v.TargetDrillId == keepId)
            .Select(v => v.SourceDrillId)
            .ToListAsync();

        foreach (var variation in variations)
        {
            // A drill is not a variation of itself, and it is not one twice.
            if (variation.SourceDrillId == keepId || keepersOwn.Contains(variation.SourceDrillId))
                _variationRepository.Delete(variation);
            else
                variation.TargetDrillId = keepId;
        }

        var links = await _pointDrillRepository.Query()
            .Where(l => l.DrillId == sourceId)
            .ToListAsync();

        var pointsAlreadyOnKeeper = await _pointDrillRepository.Query()
            .Where(l => l.DrillId == keepId)
            .Select(l => l.ImprovementPointId)
            .ToListAsync();

        foreach (var link in links)
        {
            if (pointsAlreadyOnKeeper.Contains(link.ImprovementPointId))
                _pointDrillRepository.Delete(link);
            else
                link.DrillId = keepId;
        }
    }

    private async Task RenameValuesAsync(Guid drillId, string oldName, string newName)
    {
        var uses = await _reconciler.LoadUsesAsync(drillId);
        if (uses.Count == 0) return;

        var rows = await _reconciler.ValuesForUsesAsync(uses);

        foreach (var row in rows.Where(v => v.DialName == oldName).ToList())
        {
            // A use can still hold a value under the new name, left by a dial removed earlier.
            // Two rows cannot share a name on one use, and the live dial's answer is the one
            // that means anything — so it takes the stale row's place rather than colliding
            // with it mid-save.
            var stale = rows.FirstOrDefault(v => v.DialName == newName && _reconciler.SameUse(v, row));
            if (stale is not null)
            {
                stale.Value = row.Value;
                _valueRepository.Delete(row);
            }
            else
            {
                row.DialName = newName;
            }
        }
    }

    private async Task<Drill> LoadForEditAsync(Guid drillId, Guid userId)
    {
        var drill = await _drillRepository.GetByIdWithDetailsAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        await DrillEditRules.EnsureCanEditAsync(drill, userId, _clubsClient);
        return drill;
    }

    private Task<DrillDto> ReadAsync(Guid drillId, Guid userId) => _drillService.GetByIdAsync(drillId, userId);

    private static DrillDial FindDial(Drill drill, string name) =>
        drill.Dials.FirstOrDefault(d => d.Name == name)
        ?? throw new EntityNotFoundException($"This drill has no dial called {name}");

    /// <summary>
    /// Stores the client's re-tokenized prose, but only once it agrees with the dials it is
    /// stored beside. A token with no dial renders as a literal brace on the coach's screen;
    /// a dial with no token is a control that changes nothing.
    /// </summary>
    private static void WriteInstructions(Drill drill, string html, IEnumerable<string> dialNames)
    {
        var resolved = DrillRichText.Resolve(html, null, ordered: true);
        var (unknown, unused) = DialTokens.Reconcile(resolved.Lines, dialNames);

        if (unknown.Count > 0)
            throw new BadRequestException(
                $"The instructions mention {{{unknown[0]}}}, which is not a dial on this drill",
                ErrorCodeEnum.ValidationError);

        if (unused.Count > 0)
            throw new BadRequestException(
                $"The instructions no longer mention {{{unused[0]}}}",
                ErrorCodeEnum.ValidationError);

        drill.InstructionsHtml = resolved.Html;
        drill.Instructions = resolved.Lines;
    }
}
