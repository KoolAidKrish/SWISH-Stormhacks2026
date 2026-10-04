using OpenCvSharp;

namespace HandGestureRecognition.Custom;

/// <summary>Which hand a custom gesture is made with. In the mirrored camera picture, Right is the mouse hand.</summary>
public enum GestureHand { Right, Left, Either }

/// <summary>
/// A user-recorded hand pose: the samples captured while recording (the same pose seen from several
/// angles) and how close a live hand has to come to them to count.
/// </summary>
public sealed class CustomGesture
{
    public string Name { get; set; } = "";
    public GestureHand Hand { get; set; } = GestureHand.Either;
    /// <summary>Feature vectors (see <see cref="HandFeatures"/>). Right-hand orientation unless Hand is Left.</summary>
    public List<float[]> Samples { get; set; } = [];
    /// <summary>A live hand matches when its distance to the nearest sample is below this.</summary>
    public double Threshold { get; set; }
}

/// <summary>
/// Turns 21 hand landmarks into a pose fingerprint that doesn't change when the hand moves around the
/// frame or closer to / further from the camera: positions relative to the wrist, divided by palm size
/// (wrist to middle-finger knuckle). In-plane rotation is kept on purpose, so thumbs-up and thumbs-down
/// stay different gestures. Tilting the hand towards or away from the camera does change it, which is
/// why recording collects samples from several angles.
///
/// On top of the 21 positions come 5 finger-extension values (how far each fingertip reaches, relative to
/// the palm). Those barely change when the hand tilts, but change a lot between, say, a fist and a
/// thumbs-up, which differ in only 4 of the 21 points and would otherwise look alike.
/// </summary>
public static class HandFeatures
{
    public const int Length = 42 + 5;
    const float ExtensionWeight = 4f;

    // Tip, and the point its reach is measured from: thumb from the index knuckle (a tucked thumb
    // sits against it), the other fingers from the wrist.
    static readonly (int Tip, int From)[] Reach = [(4, 5), (8, 0), (12, 0), (16, 0), (20, 0)];

    public static float[] From(IReadOnlyList<Point2f> landmarks, bool mirror = false)
    {
        var wrist = landmarks[0];
        float palm = MathF.Max(1e-3f, Dist(landmarks[9], wrist));
        var f = new float[Length];
        for (int i = 0; i < 21; i++)
        {
            float x = (landmarks[i].X - wrist.X) / palm;
            f[i * 2] = mirror ? -x : x;
            f[i * 2 + 1] = (landmarks[i].Y - wrist.Y) / palm;
        }
        for (int k = 0; k < Reach.Length; k++)
            f[42 + k] = ExtensionWeight * Dist(landmarks[Reach[k].Tip], landmarks[Reach[k].From]) / palm;
        return f;
    }

    static float Dist(Point2f a, Point2f b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }
}

/// <summary>Nearest-neighbour matching against the recorded samples. Cheap: a few hundred 42-number comparisons.</summary>
public static class GestureMatcher
{
    /// <summary>Best gesture for this hand, or null. Confidence is 1 at a perfect match, 0 at the threshold.</summary>
    public static (CustomGesture? Gesture, double Confidence) Match(IReadOnlyList<CustomGesture> gestures,
                                                                    IReadOnlyList<Point2f> landmarks, bool isRightHand)
    {
        var asIs = HandFeatures.From(landmarks);
        float[]? mirrored = null;

        CustomGesture? best = null;
        double bestRatio = double.MaxValue, secondRatio = double.MaxValue;
        foreach (var g in gestures)
        {
            if (g.Samples.Count == 0 || g.Threshold <= 0 || g.Samples[0].Length != HandFeatures.Length) continue; // empty, or recorded by an older version
            float[] f;
            if (g.Hand == GestureHand.Right) { if (!isRightHand) continue; f = asIs; }
            else if (g.Hand == GestureHand.Left) { if (isRightHand) continue; f = asIs; }
            else f = isRightHand ? asIs : (mirrored ??= HandFeatures.From(landmarks, mirror: true)); // stored in right-hand orientation

            double ratio = NearestDistance(g.Samples, f) / g.Threshold;
            if (ratio < bestRatio) { secondRatio = bestRatio; bestRatio = ratio; best = g; }
            else if (ratio < secondRatio) secondRatio = ratio;
        }

        // Must be inside its own threshold, and clearly closer to it than to any other gesture.
        if (best is null || bestRatio >= 1 || bestRatio > 0.8 * secondRatio) return (null, 0);
        return (best, 1 - bestRatio);
    }

    public static double NearestDistance(List<float[]> samples, float[] f)
    {
        double best = double.MaxValue;
        foreach (var s in samples)
        {
            double d = HandFeatures.Distance(s, f);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// Picks a gesture's threshold from its own samples: how far each sample is from the nearest *other*
    /// sample recorded at a different moment (consecutive frames are near-identical, so they're skipped).
    /// That spread is how much the pose varies across angles; a live hand within a margin of it matches.
    /// </summary>
    public static double AutoThreshold(List<float[]> samples)
    {
        const int skipNeighbours = 6;
        var nn = new List<double>();
        for (int i = 0; i < samples.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < samples.Count; j++)
                if (Math.Abs(i - j) > skipNeighbours)
                    best = Math.Min(best, HandFeatures.Distance(samples[i], samples[j]));
            if (best < double.MaxValue) nn.Add(best);
        }
        if (nn.Count == 0) return 0.5;
        nn.Sort();
        double p90 = nn[(int)(0.9 * (nn.Count - 1))];
        // 1.7x the spread, capped: measured on synthetic hands, generous enough for new angles while
        // keeping near-identical poses apart (a fist vs a thumbs-up differ only in the thumb).
        return Math.Clamp(p90 * 1.7, 0.3, 0.85);
    }

    /// <summary>Share of <paramref name="samples"/> that would be mistaken for <paramref name="other"/> (0..1).</summary>
    public static double Overlap(List<float[]> samples, CustomGesture other) =>
        samples.Count == 0 ? 0 : samples.Count(s => NearestDistance(other.Samples, s) < other.Threshold) / (double)samples.Count;
}

/// <summary>
/// Turns per-frame matches into start/end events per hand. A gesture starts after it has been the match
/// for <see cref="StableFrames"/> processed frames in a row (so passing shapes don't fire), and ends after
/// it's been missing for <see cref="ReleaseFrames"/> frames (so one bad frame doesn't end it).
/// </summary>
public sealed class GestureTracker
{
    public int StableFrames { get; init; } = 4;
    public int ReleaseFrames { get; init; } = 4;

    sealed class HandState
    {
        public CustomGesture? Candidate;
        public int CandidateFrames;
        public CustomGesture? Active;
        public int MissingFrames;
        public double Confidence;
    }

    readonly HandState _right = new(), _left = new();

    public event Action<CustomGesture, GestureHand>? Started;
    public event Action<CustomGesture, GestureHand>? Ended;

    public CustomGesture? ActiveRight => _right.Active;
    public CustomGesture? ActiveLeft => _left.Active;

    /// <summary>Feed one processed frame. Null landmarks = that hand isn't visible.</summary>
    public (CustomGesture? Right, double RightConfidence, CustomGesture? Left, double LeftConfidence) Update(
        IReadOnlyList<CustomGesture> gestures, Point2f[]? right, Point2f[]? left)
    {
        Step(_right, gestures, right, isRight: true);
        Step(_left, gestures, left, isRight: false);
        return (_right.Candidate, _right.Confidence, _left.Candidate, _left.Confidence);
    }

    void Step(HandState s, IReadOnlyList<CustomGesture> gestures, Point2f[]? landmarks, bool isRight)
    {
        var (match, confidence) = landmarks is null || gestures.Count == 0
            ? (null, 0.0)
            : GestureMatcher.Match(gestures, landmarks, isRight);
        s.Confidence = confidence;

        if (match == s.Candidate) s.CandidateFrames++;
        else { s.Candidate = match; s.CandidateFrames = 1; }

        var hand = isRight ? GestureHand.Right : GestureHand.Left;
        if (s.Active is not null && match == s.Active)
        {
            s.MissingFrames = 0;
            return;
        }
        if (s.Active is not null && ++s.MissingFrames >= ReleaseFrames)
        {
            var ended = s.Active;
            s.Active = null;
            Ended?.Invoke(ended, hand);
        }
        if (s.Active is null && match is not null && s.CandidateFrames >= StableFrames)
        {
            s.Active = match;
            s.MissingFrames = 0;
            Started?.Invoke(match, hand);
        }
    }

    /// <summary>Ends whatever is active (e.g. when gestures are edited or the engine stops).</summary>
    public void Reset()
    {
        foreach (var (s, hand) in new[] { (_right, GestureHand.Right), (_left, GestureHand.Left) })
        {
            if (s.Active is { } a) Ended?.Invoke(a, hand);
            s.Active = s.Candidate = null;
            s.CandidateFrames = s.MissingFrames = 0;
        }
    }
}
