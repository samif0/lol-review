#nullable enable

namespace LoLReview.Core.Models;

/// <summary>
/// A guided tilt check exercise entry — cognitive reappraisal (Gross 1998/2002).
/// </summary>
public class TiltCheck
{
    public int Id { get; set; }
    public string Emotion { get; set; } = "";
    public int IntensityBefore { get; set; }
    public int? IntensityAfter { get; set; }
    public string ReframeThought { get; set; } = "";
    public string ReframeResponse { get; set; } = "";
    public string ThoughtType { get; set; } = "";
    public string CueWord { get; set; } = "";
    public string FocusIntention { get; set; } = "";
    public long? CreatedAt { get; set; }
}
