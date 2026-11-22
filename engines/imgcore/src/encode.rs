//! Output encoding. JPEG uses jpeg-encoder (optimized Huffman tables, progressive, 4:2:0 below quality 90),
//! PNG is written at maximum deflate effort with adaptive filters, optionally palette-quantized.

use std::io::Cursor;

use image::{codecs::png::{CompressionType, FilterType, PngEncoder}, codecs::webp::WebPEncoder, DynamicImage, ImageEncoder, ImageFormat};
use jpeg_encoder::{ColorType, Encoder, SamplingFactor};

use crate::{bad, Failure};

pub fn resolve_format(requested: &str, input: ImageFormat) -> Result<ImageFormat, Failure> {
    Ok(match requested {
        "jpeg" | "jpg" => ImageFormat::Jpeg,
        "png" => ImageFormat::Png,
        "webp" => ImageFormat::WebP,
        "same" => match input {
            ImageFormat::Jpeg => ImageFormat::Jpeg,
            ImageFormat::WebP => ImageFormat::WebP,
            _ => ImageFormat::Png,
        },
        _ => return bad("Output format must be jpeg, png, webp or same."),
    })
}

pub fn name(format: ImageFormat) -> &'static str {
    match format {
        ImageFormat::Jpeg => "jpeg",
        ImageFormat::Png => "png",
        ImageFormat::WebP => "webp",
        ImageFormat::Gif => "gif",
        ImageFormat::Bmp => "bmp",
        ImageFormat::Tiff => "tiff",
        _ => "other",
    }
}

pub fn encode(img: &DynamicImage, format: ImageFormat, quality: u8, lossy_png: bool, target_bytes: Option<u64>)
    -> Result<(Vec<u8>, u8), Failure>
{
    match format {
        ImageFormat::Jpeg => match target_bytes {
            Some(target) => jpeg_to_target(img, target),
            None => Ok((jpeg(img, quality)?, quality)),
        },
        ImageFormat::Png => {
            let lossless = png(img)?;
            if !lossy_png {
                return Ok((lossless, quality));
            }
            // Smooth gradients can compress better in truecolour; never let "smart" compression grow the file.
            let palette = png_palette(img, quality)?;
            Ok((if palette.len() < lossless.len() { palette } else { lossless }, quality))
        }
        ImageFormat::WebP => {
            // Pure-Rust WebP is lossless; for small lossy files pick JPEG.
            let rgba = img.to_rgba8();
            let mut out = Vec::new();
            WebPEncoder::new_lossless(&mut out).write_image(&rgba, rgba.width(), rgba.height(), image::ExtendedColorType::Rgba8)?;
            Ok((out, 100))
        }
        _ => bad("Unsupported output format."),
    }
}

fn jpeg(img: &DynamicImage, quality: u8) -> Result<Vec<u8>, Failure> {
    // JPEG has no alpha: composite onto white so transparent areas don't turn black.
    let rgb = flatten_white(img);
    let mut out = Vec::new();
    let mut encoder = Encoder::new(&mut out, quality);
    encoder.set_progressive(true);
    encoder.set_optimized_huffman_tables(true);
    encoder.set_sampling_factor(if quality >= 90 { SamplingFactor::R_4_4_4 } else { SamplingFactor::R_4_2_0 });
    let (w, h) = (u16::try_from(rgb.width()), u16::try_from(rgb.height()));
    match (w, h) {
        (Ok(w), Ok(h)) => encoder.encode(rgb.as_raw(), w, h, ColorType::Rgb)?,
        _ => return bad("JPEG supports at most 65535 pixels per side."),
    }
    Ok(out)
}

/// Binary-searches the JPEG quality so the file fits the requested size; falls back to quality 5.
fn jpeg_to_target(img: &DynamicImage, target: u64) -> Result<(Vec<u8>, u8), Failure> {
    let (mut lo, mut hi) = (5u8, 95u8);
    let mut best: Option<(Vec<u8>, u8)> = None;
    while lo <= hi {
        let mid = lo + (hi - lo) / 2;
        let bytes = jpeg(img, mid)?;
        if bytes.len() as u64 <= target {
            best = Some((bytes, mid));
            lo = mid + 1;
        } else {
            if mid == 0 { break; }
            hi = mid - 1;
        }
    }
    match best {
        Some(b) => Ok(b),
        None => Ok((jpeg(img, 5)?, 5)),
    }
}

fn png(img: &DynamicImage) -> Result<Vec<u8>, Failure> {
    let mut out = Vec::new();
    let encoder = PngEncoder::new_with_quality(&mut out, CompressionType::Best, FilterType::Adaptive);
    let (w, h) = (img.width(), img.height());
    if img.color().has_alpha() {
        encoder.write_image(img.to_rgba8().as_raw(), w, h, image::ExtendedColorType::Rgba8)?;
    } else {
        encoder.write_image(img.to_rgb8().as_raw(), w, h, image::ExtendedColorType::Rgb8)?;
    }
    Ok(out)
}

/// NeuQuant palette quantization to at most 256 colours (fewer at low quality), written as an indexed PNG.
fn png_palette(img: &DynamicImage, quality: u8) -> Result<Vec<u8>, Failure> {
    let rgba = img.to_rgba8();
    let colors = (16 + (quality as usize * 240) / 100).min(256);
    // Sample factor: 1 is best/slowest, 30 fastest. Large images sample more sparsely to stay fast.
    let sample = if rgba.len() > 16_000_000 { 10 } else { 3 };
    let nq = color_quant::NeuQuant::new(sample, colors, rgba.as_raw());
    let indices: Vec<u8> = rgba.as_raw().chunks_exact(4).map(|p| nq.index_of(p) as u8).collect();
    let map = nq.color_map_rgba();
    let palette: Vec<u8> = map.chunks_exact(4).flat_map(|c| [c[0], c[1], c[2]]).collect();
    let alpha: Vec<u8> = map.chunks_exact(4).map(|c| c[3]).collect();

    let mut out = Vec::new();
    {
        let mut encoder = png::Encoder::new(Cursor::new(&mut out), rgba.width(), rgba.height());
        encoder.set_color(png::ColorType::Indexed);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.set_palette(palette);
        if alpha.iter().any(|&a| a < 255) {
            encoder.set_trns(alpha);
        }
        encoder.set_compression(png::Compression::High);
        encoder.set_filter(png::Filter::NoFilter); // filters rarely help palette images
        let mut writer = encoder.write_header()?;
        writer.write_image_data(&indices)?;
    }
    Ok(out)
}

fn flatten_white(img: &DynamicImage) -> image::RgbImage {
    if !img.color().has_alpha() {
        return img.to_rgb8();
    }
    let rgba = img.to_rgba8();
    image::RgbImage::from_fn(rgba.width(), rgba.height(), |x, y| {
        let p = rgba.get_pixel(x, y).0;
        let a = p[3] as u32;
        let blend = |c: u8| ((c as u32 * a + 255 * (255 - a)) / 255) as u8;
        image::Rgb([blend(p[0]), blend(p[1]), blend(p[2])])
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::{Rgba, RgbaImage};

    fn noisy(w: u32, h: u32) -> DynamicImage {
        DynamicImage::ImageRgba8(RgbaImage::from_fn(w, h, |x, y| {
            let v = ((x * 7919 + y * 104729) % 251) as u8;
            Rgba([v, v.wrapping_mul(3), v.wrapping_add(x as u8), 255])
        }))
    }

    #[test]
    fn target_size_is_respected() {
        let (bytes, quality) = jpeg_to_target(&noisy(400, 300), 20_000).unwrap_or_else(|_| panic!("encode"));
        assert!(bytes.len() <= 20_000 || quality == 5, "{} bytes at q{}", bytes.len(), quality);
    }

    /// Photo-like: flat colour regions with sensor-style noise (what palette quantization is meant for).
    fn photo_like(w: u32, h: u32) -> DynamicImage {
        let mut state = 0x9E37_79B9_7F4A_7C15u64; // xorshift: non-repeating noise, like a real camera sensor
        DynamicImage::ImageRgba8(RgbaImage::from_fn(w, h, |x, y| {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            let base = [(x / 50 * 40) as u8, (y / 40 * 50) as u8, ((x / 50 + y / 40) * 30) as u8];
            let n = |shift: u32| ((state >> shift) % 9) as u8;
            Rgba([base[0].saturating_add(n(0)), base[1].saturating_add(n(8)), base[2].saturating_add(n(16)), 255])
        }))
    }

    #[test]
    fn palette_png_is_smaller_and_indexed() {
        let (lossy, _) = encode(&photo_like(300, 200), ImageFormat::Png, 60, true, None).unwrap_or_else(|_| panic!("encode"));
        let full = png(&photo_like(300, 200)).unwrap_or_else(|_| panic!("png"));
        assert!(lossy.len() < full.len(), "{} vs {}", lossy.len(), full.len());
        assert_eq!(lossy[25], 3, "colour type 3 = indexed");
    }

    #[test]
    fn smart_png_never_grows_the_file() {
        let gradient = DynamicImage::ImageRgba8(RgbaImage::from_fn(300, 200, |x, y| Rgba([(x % 256) as u8, (y % 256) as u8, ((x + y) % 256) as u8, 255])));
        let (smart, _) = encode(&gradient, ImageFormat::Png, 60, true, None).unwrap_or_else(|_| panic!("encode"));
        let full = png(&gradient).unwrap_or_else(|_| panic!("png"));
        assert!(smart.len() <= full.len());
    }

    #[test]
    fn same_format_keeps_jpeg_and_webp_only() {
        assert!(matches!(resolve_format("same", ImageFormat::Jpeg), Ok(ImageFormat::Jpeg)));
        assert!(matches!(resolve_format("same", ImageFormat::Gif), Ok(ImageFormat::Png)));
        assert!(resolve_format("exe", ImageFormat::Png).is_err());
    }
}
