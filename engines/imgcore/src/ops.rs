//! Pixel operations, applied in the order the job lists them.

use fast_image_resize::{images::Image as FirImage, FilterType, ResizeAlg, ResizeOptions, Resizer};
use image::{imageops, DynamicImage, GenericImageView, Rgba, RgbaImage};
use serde::Deserialize;

use crate::{bad, Failure};

#[derive(Deserialize)]
#[serde(tag = "op", rename_all = "snake_case", deny_unknown_fields)]
pub enum Op {
    /// Crop to a rectangle in pixels (after EXIF rotation).
    Crop { x: u32, y: u32, width: u32, height: u32 },
    /// Centre-crop to an aspect ratio, e.g. 16:9.
    CropRatio { ratio_w: u32, ratio_h: u32 },
    /// Add borders to reach an aspect ratio without cutting anything off.
    PadRatio { ratio_w: u32, ratio_h: u32, #[serde(default = "white")] color: String },
    /// Resize. `fit`: "contain" keeps the ratio inside the box, "cover" fills the box and crops,
    /// "fill" stretches. A missing width or height is derived from the ratio.
    Resize {
        width: Option<u32>,
        height: Option<u32>,
        #[serde(default = "contain")] fit: String,
        #[serde(default = "yes")] upscale: bool,
    },
    /// Scale by a percentage (1-800).
    Scale { percent: f32 },
    Rotate { degrees: i32 },
    Flip { axis: String },
    /// -100..100 each.
    Adjust {
        #[serde(default)] brightness: f32,
        #[serde(default)] contrast: f32,
        #[serde(default)] saturation: f32,
    },
    /// Unsharp mask. amount 0..5, radius (sigma) 0.3..10.
    Sharpen { #[serde(default = "one")] amount: f32, #[serde(default = "one")] radius: f32 },
    /// Edge-preserving noise reduction, strength 1..3.
    Denoise { #[serde(default = "one_u8")] strength: u8 },
    /// One-click enhance: stretch levels, a touch of saturation and a gentle sharpen.
    AutoEnhance,
    Grayscale,
    /// Soften detail (the opposite of enhance), sigma 0.3..20.
    Blur { sigma: f32 },
}

fn white() -> String { "#ffffff".into() }
fn contain() -> String { "contain".into() }
fn yes() -> bool { true }
fn one() -> f32 { 1.0 }
fn one_u8() -> u8 { 1 }

pub fn apply(img: DynamicImage, op: &Op, max_pixels: u64) -> Result<DynamicImage, Failure> {
    Ok(match op {
        Op::Crop { x, y, width, height } => {
            let (w, h) = img.dimensions();
            if *width == 0 || *height == 0 || *x >= w || *y >= h {
                return bad("The crop area is outside the image.");
            }
            img.crop_imm(*x, *y, (*width).min(w - x), (*height).min(h - y))
        }
        Op::CropRatio { ratio_w, ratio_h } => {
            let (rw, rh) = ratio(*ratio_w, *ratio_h)?;
            let (w, h) = img.dimensions();
            let (cw, ch) = if (w as f64) / (h as f64) > rw / rh {
                (((h as f64) * rw / rh).round() as u32, h)
            } else {
                (w, ((w as f64) * rh / rw).round() as u32)
            };
            let (cw, ch) = (cw.clamp(1, w), ch.clamp(1, h));
            img.crop_imm((w - cw) / 2, (h - ch) / 2, cw, ch)
        }
        Op::PadRatio { ratio_w, ratio_h, color } => {
            let (rw, rh) = ratio(*ratio_w, *ratio_h)?;
            let (w, h) = img.dimensions();
            let (cw, ch) = if (w as f64) / (h as f64) > rw / rh {
                (w, ((w as f64) * rh / rw).round() as u32)
            } else {
                (((h as f64) * rw / rh).round() as u32, h)
            };
            check_pixels(cw, ch, max_pixels)?;
            let mut canvas = RgbaImage::from_pixel(cw, ch, parse_color(color)?);
            imageops::overlay(&mut canvas, &img.to_rgba8(), ((cw - w) / 2) as i64, ((ch - h) / 2) as i64);
            DynamicImage::ImageRgba8(canvas)
        }
        Op::Resize { width, height, fit, upscale } => resize_box(img, *width, *height, fit, *upscale, max_pixels)?,
        Op::Scale { percent } => {
            if !(1.0..=800.0).contains(percent) {
                return bad("Scale must be between 1% and 800%.");
            }
            let (w, h) = img.dimensions();
            let nw = ((w as f32) * percent / 100.0).round().max(1.0) as u32;
            let nh = ((h as f32) * percent / 100.0).round().max(1.0) as u32;
            resize_exact(&img, nw, nh, max_pixels)?
        }
        Op::Rotate { degrees } => match degrees.rem_euclid(360) {
            0 => img,
            90 => img.rotate90(),
            180 => img.rotate180(),
            270 => img.rotate270(),
            _ => return bad("Rotation must be a multiple of 90 degrees."),
        },
        Op::Flip { axis } => match axis.as_str() {
            "horizontal" => img.fliph(),
            "vertical" => img.flipv(),
            _ => return bad("Flip axis must be horizontal or vertical."),
        },
        Op::Adjust { brightness, contrast, saturation } => adjust(img, *brightness, *contrast, *saturation),
        Op::Sharpen { amount, radius } => sharpen(&img, amount.clamp(0.0, 5.0), radius.clamp(0.3, 10.0)),
        Op::Denoise { strength } => denoise(&img, (*strength).clamp(1, 3)),
        Op::AutoEnhance => {
            let leveled = auto_levels(img);
            let vivid = adjust(leveled, 0.0, 4.0, 10.0);
            sharpen(&vivid, 0.6, 1.0)
        }
        Op::Grayscale => DynamicImage::ImageLuma8(img.to_luma8()),
        Op::Blur { sigma } => img.blur(sigma.clamp(0.3, 20.0)),
    })
}

fn ratio(w: u32, h: u32) -> Result<(f64, f64), Failure> {
    if w == 0 || h == 0 || w > 100 || h > 100 {
        return bad("Aspect ratio parts must be between 1 and 100.");
    }
    Ok((w as f64, h as f64))
}

fn check_pixels(w: u32, h: u32, max_pixels: u64) -> Result<(), Failure> {
    if (w as u64) * (h as u64) > max_pixels {
        return bad(format!("The result would be {w}×{h}; the limit is {} megapixels.", max_pixels / 1_000_000));
    }
    Ok(())
}

fn parse_color(s: &str) -> Result<Rgba<u8>, Failure> {
    let hex = s.trim_start_matches('#');
    let parse = |i: usize| u8::from_str_radix(hex.get(i..i + 2).unwrap_or("zz"), 16);
    match hex.len() {
        6 => Ok(Rgba([parse(0)?, parse(2)?, parse(4)?, 255])),
        8 => Ok(Rgba([parse(0)?, parse(2)?, parse(4)?, parse(6)?])),
        _ => bad("Colours must look like #rrggbb."),
    }
}

fn resize_box(img: DynamicImage, width: Option<u32>, height: Option<u32>, fit: &str, upscale: bool, max_pixels: u64)
    -> Result<DynamicImage, Failure>
{
    let (w, h) = (img.width() as f64, img.height() as f64);
    let (bw, bh) = match (width, height) {
        (Some(bw), Some(bh)) if bw > 0 && bh > 0 => (bw as f64, bh as f64),
        (Some(bw), None) if bw > 0 => (bw as f64, (bw as f64 * h / w).round().max(1.0)),
        (None, Some(bh)) if bh > 0 => ((bh as f64 * w / h).round().max(1.0), bh as f64),
        _ => return bad("Give a width, a height, or both."),
    };

    let (tw, th) = match fit {
        "fill" => (bw, bh),
        "contain" => { let s = (bw / w).min(bh / h); (w * s, h * s) }
        "cover" => { let s = (bw / w).max(bh / h); (w * s, h * s) }
        _ => return bad("Fit must be contain, cover or fill."),
    };
    let (mut tw, mut th) = (tw.round().max(1.0) as u32, th.round().max(1.0) as u32);
    if !upscale && (tw as f64 > w || th as f64 > h) {
        (tw, th) = (img.width(), img.height());
    }
    let resized = if (tw, th) == img.dimensions() { img } else { resize_exact(&img, tw, th, max_pixels)? };

    if fit == "cover" {
        let (bw, bh) = (bw as u32, bh as u32);
        let (rw, rh) = resized.dimensions();
        if rw > bw || rh > bh {
            let (cw, ch) = (bw.min(rw), bh.min(rh));
            return Ok(resized.crop_imm((rw - cw) / 2, (rh - ch) / 2, cw, ch));
        }
    }
    Ok(resized)
}

/// High-quality SIMD resize (Lanczos3) via fast_image_resize; ~10x faster than image's own filters.
pub fn resize_exact(img: &DynamicImage, w: u32, h: u32, max_pixels: u64) -> Result<DynamicImage, Failure> {
    check_pixels(w, h, max_pixels)?;
    let src = img.to_rgba8();
    let src_view = FirImage::from_vec_u8(src.width(), src.height(), src.into_raw(), fast_image_resize::PixelType::U8x4)?;
    let mut dst = FirImage::new(w, h, fast_image_resize::PixelType::U8x4);
    let options = ResizeOptions::new().resize_alg(ResizeAlg::Convolution(FilterType::Lanczos3)).use_alpha(true);
    Resizer::new().resize(&src_view, &mut dst, &options)?;
    let out = RgbaImage::from_raw(w, h, dst.into_vec()).ok_or_else(|| Failure::Internal("resize buffer".into()))?;
    Ok(DynamicImage::ImageRgba8(out))
}

fn adjust(img: DynamicImage, brightness: f32, contrast: f32, saturation: f32) -> DynamicImage {
    let b = brightness.clamp(-100.0, 100.0) * 2.55;
    let c = contrast.clamp(-100.0, 100.0);
    let cf = (259.0 * (c * 2.55 + 255.0)) / (255.0 * (259.0 - c * 2.55));
    let s = 1.0 + saturation.clamp(-100.0, 100.0) / 100.0;
    let mut rgba = img.to_rgba8();
    for p in rgba.pixels_mut() {
        let [r, g, bl, a] = p.0;
        let (mut r, mut g, mut bl) = (r as f32, g as f32, bl as f32);
        // Saturation around the pixel's luma, then contrast around mid-grey, then brightness.
        let luma = 0.299 * r + 0.587 * g + 0.114 * bl;
        r = luma + (r - luma) * s;
        g = luma + (g - luma) * s;
        bl = luma + (bl - luma) * s;
        let f = |v: f32| (cf * (v - 128.0) + 128.0 + b).clamp(0.0, 255.0) as u8;
        p.0 = [f(r), f(g), f(bl), a];
    }
    DynamicImage::ImageRgba8(rgba)
}

fn sharpen(img: &DynamicImage, amount: f32, radius: f32) -> DynamicImage {
    if amount <= 0.0 {
        return img.clone();
    }
    let original = img.to_rgba8();
    let blurred = imageops::blur(&original, radius);
    let mut out = original.clone();
    for (o, (src, bl)) in out.pixels_mut().zip(original.pixels().zip(blurred.pixels())) {
        for i in 0..3 {
            let v = src.0[i] as f32 + amount * (src.0[i] as f32 - bl.0[i] as f32);
            o.0[i] = v.clamp(0.0, 255.0) as u8;
        }
    }
    DynamicImage::ImageRgba8(out)
}

/// Bilateral filter: averages neighbours that look alike, so noise goes and edges stay.
fn denoise(img: &DynamicImage, strength: u8) -> DynamicImage {
    let src = img.to_rgba8();
    let (w, h) = src.dimensions();
    let radius = strength as i32;
    let sigma_r = 12.0 + 10.0 * strength as f32;
    let sigma_s = radius as f32;
    let range_lut: Vec<f32> = (0..=765).map(|d| (-(d as f32).powi(2) / (2.0 * sigma_r * sigma_r * 3.0)).exp()).collect();
    let spatial: Vec<f32> = (-radius..=radius)
        .flat_map(|dy| (-radius..=radius).map(move |dx| (-((dx * dx + dy * dy) as f32) / (2.0 * sigma_s * sigma_s)).exp()))
        .collect();

    let mut out = RgbaImage::new(w, h);
    for y in 0..h as i32 {
        for x in 0..w as i32 {
            let c = src.get_pixel(x as u32, y as u32).0;
            let (mut acc, mut total) = ([0f32; 3], 0f32);
            let mut k = 0;
            for dy in -radius..=radius {
                for dx in -radius..=radius {
                    let (nx, ny) = ((x + dx).clamp(0, w as i32 - 1) as u32, (y + dy).clamp(0, h as i32 - 1) as u32);
                    let n = src.get_pixel(nx, ny).0;
                    let diff = (c[0] as i32 - n[0] as i32).unsigned_abs()
                        + (c[1] as i32 - n[1] as i32).unsigned_abs()
                        + (c[2] as i32 - n[2] as i32).unsigned_abs();
                    let weight = spatial[k] * range_lut[diff as usize];
                    k += 1;
                    for i in 0..3 {
                        acc[i] += n[i] as f32 * weight;
                    }
                    total += weight;
                }
            }
            out.put_pixel(x as u32, y as u32, Rgba([
                (acc[0] / total).round() as u8,
                (acc[1] / total).round() as u8,
                (acc[2] / total).round() as u8,
                c[3],
            ]));
        }
    }
    DynamicImage::ImageRgba8(out)
}

/// Stretches each channel so the darkest 0.5% becomes black and the brightest 0.5% white.
fn auto_levels(img: DynamicImage) -> DynamicImage {
    let mut rgba = img.to_rgba8();
    let total = (rgba.width() as u64 * rgba.height() as u64).max(1);
    let clip = total / 200;
    let mut lut = [[0u8; 256]; 3];
    for (ch, table) in lut.iter_mut().enumerate() {
        let mut hist = [0u64; 256];
        for p in rgba.pixels() {
            hist[p.0[ch] as usize] += 1;
        }
        let (mut lo, mut acc) = (0usize, 0u64);
        while lo < 255 && acc + hist[lo] <= clip { acc += hist[lo]; lo += 1; }
        let (mut hi, mut acc) = (255usize, 0u64);
        while hi > 0 && acc + hist[hi] <= clip { acc += hist[hi]; hi -= 1; }
        for (v, slot) in table.iter_mut().enumerate() {
            *slot = if hi <= lo + 10 {
                v as u8 // nearly flat channel: leave it alone
            } else {
                (((v as f32 - lo as f32) * 255.0 / (hi - lo) as f32).clamp(0.0, 255.0)) as u8
            };
        }
    }
    for p in rgba.pixels_mut() {
        for (i, table) in lut.iter().enumerate() {
            p.0[i] = table[p.0[i] as usize];
        }
    }
    DynamicImage::ImageRgba8(rgba)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn img(w: u32, h: u32) -> DynamicImage {
        DynamicImage::ImageRgba8(RgbaImage::from_fn(w, h, |x, y| Rgba([(x % 256) as u8, (y % 256) as u8, 128, 255])))
    }

    fn run(i: DynamicImage, op: Op) -> DynamicImage {
        match apply(i, &op, 50_000_000) {
            Ok(out) => out,
            Err(Failure::BadInput(m) | Failure::Internal(m)) => panic!("{m}"),
        }
    }

    #[test]
    fn crop_ratio_centres_the_largest_box() {
        assert_eq!(run(img(1600, 1000), Op::CropRatio { ratio_w: 1, ratio_h: 1 }).dimensions(), (1000, 1000));
        assert_eq!(run(img(1000, 1600), Op::CropRatio { ratio_w: 16, ratio_h: 9 }).dimensions(), (1000, 563));
    }

    #[test]
    fn pad_ratio_adds_borders() {
        assert_eq!(run(img(160, 160), Op::PadRatio { ratio_w: 16, ratio_h: 9, color: "#000000".into() }).dimensions(), (284, 160));
    }

    #[test]
    fn resize_fits() {
        let r = |w, h, fit: &str| run(img(800, 400), Op::Resize { width: w, height: h, fit: fit.into(), upscale: true }).dimensions();
        assert_eq!(r(Some(400), None, "contain"), (400, 200));
        assert_eq!(r(Some(300), Some(300), "contain"), (300, 150));
        assert_eq!(r(Some(300), Some(300), "cover"), (300, 300));
        assert_eq!(r(Some(100), Some(500), "fill"), (100, 500));
        let no_up = run(img(100, 50), Op::Resize { width: Some(400), height: None, fit: "contain".into(), upscale: false });
        assert_eq!(no_up.dimensions(), (100, 50));
    }

    #[test]
    fn rotate_and_crop_bounds() {
        assert_eq!(run(img(30, 10), Op::Rotate { degrees: -90 }).dimensions(), (10, 30));
        assert_eq!(run(img(30, 10), Op::Crop { x: 25, y: 5, width: 100, height: 100 }).dimensions(), (5, 5));
        assert!(apply(img(30, 10), &Op::Crop { x: 30, y: 0, width: 1, height: 1 }, 1_000_000).is_err());
        assert!(apply(img(30, 10), &Op::Rotate { degrees: 45 }, 1_000_000).is_err());
    }

    #[test]
    fn pixel_limit_applies_to_results() {
        assert!(apply(img(100, 100), &Op::Scale { percent: 800.0 }, 100_000).is_err());
    }

    #[test]
    fn colours_parse() {
        assert_eq!(parse_color("#ff8000").ok(), Some(Rgba([255, 128, 0, 255])));
        assert!(parse_color("red").is_err());
        assert!(parse_color("#zzzzzz").is_err());
    }

    #[test]
    fn enhance_filters_keep_size() {
        for op in [Op::AutoEnhance, Op::Denoise { strength: 2 }, Op::Sharpen { amount: 1.0, radius: 1.0 }, Op::Blur { sigma: 2.0 }, Op::Grayscale,
                   Op::Adjust { brightness: 10.0, contrast: -10.0, saturation: 50.0 }] {
            assert_eq!(run(img(40, 30), op).dimensions(), (40, 30));
        }
    }
}
