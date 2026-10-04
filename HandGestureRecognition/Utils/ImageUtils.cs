using System.Runtime.InteropServices;
using OpenCvSharp;

namespace HandGestureRecognition.Utils;

/// <summary>
/// A (possibly rotated) rectangle: center, size and clockwise rotation in degrees.
/// Equivalent of one row of the Python float32 array [cx, cy, width, height, angle].
/// </summary>
public readonly record struct RotRect(float Cx, float Cy, float Width, float Height, float Angle);

public enum CropOutOfRangeOperation
{
    /// <summary>Pad the image so rotated crops never fall outside it.</summary>
    Padding,
    /// <summary>Drop rectangles that are not fully inside the image.</summary>
    Ignore,
}

/// <summary>Port of utils/utils.py (image processing helpers).</summary>
public static class ImageUtils
{
    /// <summary>Python-style floor division for ints (C# '/' truncates toward zero).</summary>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) ^ (b < 0)))
        {
            q--;
        }
        return q;
    }

    public static double NormalizeRadians(double angle)
    {
        return angle - 2 * Math.PI * Math.Floor((angle + Math.PI) / (2 * Math.PI));
    }

    /// <summary>Determines whether each rect lies entirely inside the outer (upright) rectangle.</summary>
    public static bool[] IsInsideRect(IReadOnlyList<RotRect> rects, int widthOfOuterRect, int heightOfOuterRect)
    {
        var results = new bool[rects.Count];

        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];

            if (r.Cx < 0 || r.Cx > widthOfOuterRect)
            {
                results[i] = false;
            }
            else if (r.Cy < 0 || r.Cy > heightOfOuterRect)
            {
                results[i] = false;
            }
            else
            {
                var (xMin, xMax, yMin, yMax) = BoxExtents(r);
                results[i] = xMin >= 0 && xMax <= widthOfOuterRect && yMin >= 0 && yMax <= heightOfOuterRect;
            }
        }

        return results;
    }

    /// <summary>Converts rotated rectangles to their upright bounding boxes (angle = 0).</summary>
    public static List<RotRect> BoundingBoxFromRotatedRect(IReadOnlyList<RotRect> rects)
    {
        var results = new List<RotRect>(rects.Count);

        foreach (var r in rects)
        {
            var (xMin, xMax, yMin, yMax) = BoxExtents(r);
            int cx = FloorDiv(xMin + xMax, 2);
            int cy = FloorDiv(yMin + yMax, 2);
            int width = xMax - xMin;
            int height = yMax - yMin;
            results.Add(new RotRect(cx, cy, width, height, 0));
        }

        return results;
    }

    /// <summary>Rotates each image by its angle (degrees) and enlarges the canvas so nothing is cropped.</summary>
    public static List<Mat> ImageRotationWithoutCrop(IReadOnlyList<Mat> images, IReadOnlyList<float> angles)
    {
        var rotatedImages = new List<Mat>();
        int n = Math.Min(images.Count, angles.Count);

        for (int i = 0; i < n; i++)
        {
            var image = images[i];
            int height = image.Rows;
            int width = image.Cols;
            var imageCenter = new Point2f(width / 2, height / 2);

            using var rotationMatrix = Cv2.GetRotationMatrix2D(imageCenter, (int)angles[i], 1.0);
            double absCos = Math.Abs(rotationMatrix.Get<double>(0, 0));
            double absSin = Math.Abs(rotationMatrix.Get<double>(0, 1));
            int boundW = (int)(height * absSin + width * absCos);
            int boundH = (int)(height * absCos + width * absSin);
            rotationMatrix.Set(0, 2, rotationMatrix.Get<double>(0, 2) + boundW / 2.0 - imageCenter.X);
            rotationMatrix.Set(1, 2, rotationMatrix.Get<double>(1, 2) + boundH / 2.0 - imageCenter.Y);

            var rotatedImage = new Mat();
            Cv2.WarpAffine(image, rotatedImage, rotationMatrix, new Size(boundW, boundH));
            rotatedImages.Add(rotatedImage);
        }

        return rotatedImages;
    }

    /// <summary>Crops upright rectangles from the image. Rects outside the image are skipped.</summary>
    public static List<Mat> CropRectangle(Mat image, IReadOnlyList<RotRect> rects)
    {
        var croppedImages = new List<Mat>();
        int height = image.Rows;
        int width = image.Cols;

        var insideOrOutsides = IsInsideRect(rects, width, height);

        for (int i = 0; i < rects.Count; i++)
        {
            if (!insideOrOutsides[i])
            {
                continue;
            }

            var r = rects[i];
            int cx = (int)r.Cx;
            int cy = (int)r.Cy;
            int rectWidth = (int)r.Width;
            int rectHeight = (int)r.Height;

            var roi = ClampRoi(
                cx - FloorDiv(rectWidth, 2),
                cy - FloorDiv(rectHeight, 2),
                rectWidth,
                rectHeight,
                width,
                height);
            using var sub = new Mat(image, roi);
            croppedImages.Add(sub.Clone());
        }

        return croppedImages;
    }

    /// <summary>Crops rotated rectangles out of an image, returning them de-rotated (upright).</summary>
    public static List<Mat> RotateAndCropRectangle(
        Mat image,
        IReadOnlyList<RotRect> rectsTmp,
        CropOutOfRangeOperation operationWhenCroppingOutOfRange)
    {
        var rects = rectsTmp.ToList();
        var rotatedCroppedImages = new List<Mat>();
        int height = image.Rows;
        int width = image.Cols;

        Mat workImage = image;
        Mat? paddedImage = null;

        if (operationWhenCroppingOutOfRange == CropOutOfRangeOperation.Padding)
        {
            int size = ((int)Math.Sqrt(width * width + height * height) + 2) * 2;
            paddedImage = PadImage(image, size, size);
            workImage = paddedImage;
            double offsetX = Math.Abs(size - width) / 2.0;
            double offsetY = Math.Abs(size - height) / 2.0;
            rects = rects
                .Select(r => r with { Cx = (float)(r.Cx + offsetX), Cy = (float)(r.Cy + offsetY) })
                .ToList();
        }
        else if (operationWhenCroppingOutOfRange == CropOutOfRangeOperation.Ignore)
        {
            var inside = IsInsideRect(rects, width, height);
            rects = rects.Where((_, i) => inside[i]).ToList();
        }

        var rectBbxUpright = BoundingBoxFromRotatedRect(rects);
        var rectBbxUprightImages = CropRectangle(workImage, rectBbxUpright);
        var rotatedRectBbxUprightImages = ImageRotationWithoutCrop(
            rectBbxUprightImages,
            rects.Select(r => r.Angle).ToList());

        int n = Math.Min(rotatedRectBbxUprightImages.Count, rects.Count);
        for (int i = 0; i < n; i++)
        {
            var rotated = rotatedRectBbxUprightImages[i];
            var rect = rects[i];
            int cropCx = rotated.Cols / 2;
            int cropCy = rotated.Rows / 2;
            int rectWidth = (int)rect.Width;
            int rectHeight = (int)rect.Height;

            // NumPy slicing silently clamps out-of-range indices; ClampRoi reproduces that
            // (and guarantees at least 1x1 so downstream resizes don't throw).
            var roi = ClampRoi(
                cropCx - FloorDiv(rectWidth, 2),
                cropCy - FloorDiv(rectHeight, 2),
                rectWidth,
                rectHeight,
                rotated.Cols,
                rotated.Rows);
            using var sub = new Mat(rotated, roi);
            rotatedCroppedImages.Add(sub.Clone());
        }

        foreach (var m in rectBbxUprightImages) m.Dispose();
        foreach (var m in rotatedRectBbxUprightImages) m.Dispose();
        paddedImage?.Dispose();

        return rotatedCroppedImages;
    }

    /// <summary>
    /// Resize to fit inside (resizeWidth x resizeHeight) keeping aspect ratio, then pad
    /// the short side with black. Returns (padded, resized-before-padding).
    /// </summary>
    public static (Mat Padded, Mat Resized) KeepAspectResizeAndPad(Mat image, int resizeWidth, int resizeHeight)
    {
        int imageHeight = image.Rows;
        int imageWidth = image.Cols;
        var paddedImage = new Mat(resizeHeight, resizeWidth, MatType.CV_8UC3, Scalar.All(0));

        double ash = resizeHeight / (double)imageHeight;
        double asw = resizeWidth / (double)imageWidth;
        Size sizeAs = asw < ash
            ? new Size((int)(imageWidth * asw), (int)(imageHeight * asw))
            : new Size((int)(imageWidth * ash), (int)(imageHeight * ash));

        var resizedImage = new Mat();
        Cv2.Resize(image, resizedImage, sizeAs);

        int startH = (int)(resizeHeight / 2.0 - sizeAs.Height / 2.0);
        int startW = (int)(resizeWidth / 2.0 - sizeAs.Width / 2.0);
        using (var roi = new Mat(paddedImage, new Rect(startW, startH, sizeAs.Width, sizeAs.Height)))
        {
            resizedImage.CopyTo(roi);
        }

        return (paddedImage, resizedImage);
    }

    /// <summary>Pads the image (centered) to at least the given size.</summary>
    public static Mat PadImage(Mat image, int resizeWidth, int resizeHeight)
    {
        int imageHeight = image.Rows;
        int imageWidth = image.Cols;

        if (resizeWidth < imageWidth) resizeWidth = imageWidth;
        if (resizeHeight < imageHeight) resizeHeight = imageHeight;

        var paddedImage = new Mat(resizeHeight, resizeWidth, MatType.CV_8UC3, Scalar.All(0));
        int startH = (int)(resizeHeight / 2.0 - imageHeight / 2.0);
        int startW = (int)(resizeWidth / 2.0 - imageWidth / 2.0);
        using (var roi = new Mat(paddedImage, new Rect(startW, startH, imageWidth, imageHeight)))
        {
            image.CopyTo(roi);
        }

        return paddedImage;
    }

    /// <summary>Four corner points for drawing a rotated rectangle (rotation in radians).</summary>
    public static Point[] RotatedRectToPoints(double cx, double cy, double width, double height, double rotation)
    {
        double b = Math.Cos(rotation) * 0.5;
        double a = Math.Sin(rotation) * 0.5;
        double p0x = cx - a * height - b * width;
        double p0y = cy + b * height - a * width;
        double p1x = cx + a * height - b * width;
        double p1y = cy - b * height - a * width;
        int p2x = (int)(2 * cx - p0x);
        int p2y = (int)(2 * cy - p0y);
        int p3x = (int)(2 * cx - p1x);
        int p3y = (int)(2 * cy - p1y);

        return new[]
        {
            new Point((int)p0x, (int)p0y),
            new Point((int)p1x, (int)p1y),
            new Point(p2x, p2y),
            new Point(p3x, p3y),
        };
    }

    /// <summary>
    /// BGR uint8 HWC image → RGB float32 CHW in [0,1], written into dest at offset.
    /// Equivalent of: img / 255.0 → [..., ::-1] → transpose(2,0,1).
    /// </summary>
    public static void BgrToRgbChwNormalized(Mat bgr, float[] dest, int offset)
    {
        int h = bgr.Rows;
        int w = bgr.Cols;
        int plane = h * w;
        var buffer = new byte[plane * 3];

        using (var continuous = bgr.IsContinuous() ? null : bgr.Clone())
        {
            Marshal.Copy((continuous ?? bgr).Data, buffer, 0, buffer.Length);
        }

        for (int p = 0; p < plane; p++)
        {
            int src = p * 3;
            dest[offset + p] = (float)(buffer[src + 2] / 255.0);              // R
            dest[offset + plane + p] = (float)(buffer[src + 1] / 255.0);      // G
            dest[offset + 2 * plane + p] = (float)(buffer[src] / 255.0);      // B
        }
    }

    private static (int XMin, int XMax, int YMin, int YMax) BoxExtents(RotRect r)
    {
        var box = Cv2.BoxPoints(new RotatedRect(new Point2f(r.Cx, r.Cy), new Size2f(r.Width, r.Height), r.Angle));
        return (
            (int)box.Min(p => p.X),
            (int)box.Max(p => p.X),
            (int)box.Min(p => p.Y),
            (int)box.Max(p => p.Y));
    }

    private static Rect ClampRoi(int x, int y, int w, int h, int maxW, int maxH)
    {
        int x0 = Math.Clamp(x, 0, Math.Max(0, maxW - 1));
        int y0 = Math.Clamp(y, 0, Math.Max(0, maxH - 1));
        int x1 = Math.Clamp(x + w, x0 + 1, maxW);
        int y1 = Math.Clamp(y + h, y0 + 1, maxH);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
