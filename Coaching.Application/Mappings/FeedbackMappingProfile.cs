using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;

namespace Coaching.Application.Mappings;

public class FeedbackMappingProfile : Profile
{
    public FeedbackMappingProfile()
    {
        CreateMap<Feedback, FeedbackDto>()
            .ForMember(d => d.RecipientName, opt => opt.Ignore())
            .ForMember(d => d.RecipientImageUrl, opt => opt.Ignore())
            .ForMember(d => d.CoachName, opt => opt.Ignore())
            .ForMember(d => d.CoachImageUrl, opt => opt.Ignore())
            .ForMember(d => d.Event, opt => opt.Ignore())
            .ForMember(d => d.Attachments, opt => opt.MapFrom(s => s.Media))
            .ForMember(d => d.Praise, opt => opt.MapFrom(s => s.LivePraise()));
        CreateMap<ImprovementPoint, ImprovementPointDto>()
            .ForMember(d => d.AttachedDrills, opt => opt.MapFrom(s =>
                s.AttachedDrills.Select(ad => new AttachedDrillReferenceDto
                {
                    DrillId = ad.DrillId,
                    Name = ad.Drill != null ? ad.Drill.Name : null,
                    Category = ad.Drill != null ? ad.Drill.Category : null,
                    Intensity = ad.Drill != null ? ad.Drill.Intensity : null,
                })));
        // The stored Source is whatever the client sent, and the phone sends none, so a file it
        // uploaded reads as a Link. The URL answers it, as it does for a feedback's own attachments.
        CreateMap<ImprovementPointMedia, ImprovementPointMediaDto>()
            .ForMember(d => d.Url, opt => opt.MapFrom<SignedImprovementPointMediaUrlResolver>())
            .ForMember(d => d.Source, opt => opt.MapFrom<ImprovementPointMediaSourceResolver>());
        CreateMap<FeedbackMedia, FeedbackMediaDto>()
            .ForMember(d => d.Url, opt => opt.MapFrom<SignedFeedbackMediaUrlResolver>())
            .ForMember(d => d.Source, opt => opt.MapFrom<FeedbackMediaSourceResolver>());
        // Reads sign a stored file's URL on the way out, so writes undo it on the way in.
        CreateMap<CreateFeedbackMediaDto, FeedbackMedia>()
            .ForMember(d => d.Url, opt => opt.MapFrom<StoredFeedbackMediaUrlResolver>())
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.FeedbackId, opt => opt.Ignore())
            .ForMember(d => d.Order, opt => opt.Ignore())
            .ForMember(d => d.Feedback, opt => opt.Ignore());
        CreateMap<Praise, PraiseDto>();

        CreateMap<CreateFeedbackDto, Feedback>()
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.CoachUserId, opt => opt.Ignore())
            .ForMember(d => d.ContentPlainText, opt => opt.Ignore())
            .ForMember(d => d.Evaluation, opt => opt.Ignore())
            .ForMember(d => d.ImprovementPoints, opt => opt.Ignore())
            .ForMember(d => d.Media, opt => opt.Ignore())
            .ForMember(d => d.Praise, opt => opt.Ignore())
            // Phase A: If Content is null but Comment is provided (old client), use Comment as Content
            .ForMember(d => d.Content, opt => opt.MapFrom(s => s.Content ?? s.Comment))
            // Phase A: Keep Comment column in sync for rollback safety
            .ForMember(d => d.Comment, opt => opt.Ignore()); // Set in service after sanitization

        CreateMap<CreateImprovementPointDto, ImprovementPoint>()
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.FeedbackId, opt => opt.Ignore())
            .ForMember(d => d.Feedback, opt => opt.Ignore())
            .ForMember(d => d.Order, opt => opt.Ignore())
            .ForMember(d => d.AttachedDrills, opt => opt.Ignore())
            .ForMember(d => d.MediaLinks, opt => opt.Ignore());

        CreateMap<CreateImprovementPointMediaDto, ImprovementPointMedia>()
            .ForMember(d => d.Url, opt => opt.MapFrom<StoredImprovementPointMediaUrlResolver>())
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.ImprovementPointId, opt => opt.Ignore())
            .ForMember(d => d.ImprovementPoint, opt => opt.Ignore());

        CreateMap<CreatePraiseDto, Praise>()
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.FeedbackId, opt => opt.Ignore())
            .ForMember(d => d.Feedback, opt => opt.Ignore());
    }
}

public class SignedFeedbackMediaUrlResolver(IFeedbackMediaUrlSigner signer)
    : IValueResolver<FeedbackMedia, FeedbackMediaDto, string>
{
    public string Resolve(FeedbackMedia source, FeedbackMediaDto destination, string destMember, ResolutionContext context) =>
        signer.SignReadUrl(source.Url);
}

public class FeedbackMediaSourceResolver(IFeedbackMediaUrlSigner signer)
    : IValueResolver<FeedbackMedia, FeedbackMediaDto, FeedbackMediaSource>
{
    public FeedbackMediaSource Resolve(FeedbackMedia source, FeedbackMediaDto destination, FeedbackMediaSource destMember, ResolutionContext context) =>
        signer.IsStored(source.Url) ? FeedbackMediaSource.File : FeedbackMediaSource.Link;
}

public class SignedImprovementPointMediaUrlResolver(IFeedbackMediaUrlSigner signer)
    : IValueResolver<ImprovementPointMedia, ImprovementPointMediaDto, string>
{
    public string Resolve(ImprovementPointMedia source, ImprovementPointMediaDto destination, string destMember, ResolutionContext context) =>
        signer.SignReadUrl(source.Url);
}

public class ImprovementPointMediaSourceResolver(IFeedbackMediaUrlSigner signer)
    : IValueResolver<ImprovementPointMedia, ImprovementPointMediaDto, FeedbackMediaSource>
{
    public FeedbackMediaSource Resolve(ImprovementPointMedia source, ImprovementPointMediaDto destination, FeedbackMediaSource destMember, ResolutionContext context) =>
        signer.IsStored(source.Url) ? FeedbackMediaSource.File : FeedbackMediaSource.Link;
}

public class StoredFeedbackMediaUrlResolver(IFeedbackMediaUrlSigner signer)
    : IValueResolver<CreateFeedbackMediaDto, FeedbackMedia, string>
{
    public string Resolve(CreateFeedbackMediaDto source, FeedbackMedia destination, string destMember, ResolutionContext context) =>
        signer.ToStoredUrl(source.Url);
}

public class StoredImprovementPointMediaUrlResolver(IFeedbackMediaUrlSigner signer)
    : IValueResolver<CreateImprovementPointMediaDto, ImprovementPointMedia, string>
{
    public string Resolve(CreateImprovementPointMediaDto source, ImprovementPointMedia destination, string destMember, ResolutionContext context) =>
        signer.ToStoredUrl(source.Url);
}
