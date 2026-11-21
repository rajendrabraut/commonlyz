//! imgcore: image processing engine for Commonlyz Files.
//!
//! Reads one JSON job from stdin, writes the result image to `output` and prints a JSON summary on stdout.
//! Errors go to stdout as `{"error": "..."}` with exit code 2 (bad input) or 1 (internal).
//! Everything is pure Rust, so decoding untrusted uploads cannot corrupt memory, and decoder limits stop
//! decompression bombs before any pixel buffer is allocated.

mod ops;
mod encode;

use std::io::{BufReader, Read};
use std::process::ExitCode;

use image::{DynamicImage, ImageDecoder, ImageReader, Limits};
use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Job {
    pub input: String,
    pub output: String,
    /// "jpeg" | "png" | "webp" | "same"
    #[serde(default = "default_format")]
    pub format: String,
    /// 1..=100, used by JPEG (and by PNG when `lossy_png` is set, as the palette budget).
    #[serde(default = "default_quality")]
    pub quality: u8,
    /// Quantize PNGs to a 256-colour palette (TinyPNG-style), usually 60-80% smaller.
    #[serde(default)]
    pub lossy_png: bool,
    /// Pick the highest JPEG quality that fits in this many bytes (JPEG output only).
    #[serde(default)]
    pub target_bytes: Option<u64>,
    #[serde(default = "default_max_pixels")]
    pub max_pixels: u64,
    #[serde(default)]
    pub ops: Vec<ops::Op>,
}

fn default_format() -> String { "same".into() }
fn default_quality() -> u8 { 82 }
fn default_max_pixels() -> u64 { 50_000_000 }

#[derive(Serialize)]
struct Summary {
    width: u32,
    height: u32,
    bytes: u64,
    format: &'static str,
    quality: u8,
    input_format: &'static str,
    input_width: u32,
    input_height: u32,
}

/// Errors caused by the input (bad file, bad options) vs. by the engine itself.
pub enum Failure {
    BadInput(String),
    Internal(String),
}

impl<E: std::fmt::Display> From<E> for Failure {
    fn from(e: E) -> Self { Failure::Internal(e.to_string()) }
}

pub fn bad<T>(msg: impl Into<String>) -> Result<T, Failure> { Err(Failure::BadInput(msg.into())) }

fn main() -> ExitCode {
    let mut raw = String::new();
    let result = std::io::stdin().take(1 << 20).read_to_string(&mut raw)
        .map_err(Failure::from)
        // Some shells prepend a UTF-8 byte-order mark when piping; it isn't valid JSON, so drop it.
        .and_then(|_| serde_json::from_str::<Job>(raw.trim_start_matches('\u{feff}')).map_err(|e| Failure::BadInput(format!("invalid job: {e}"))))
        .and_then(run);

    match result {
        Ok(summary) => {
            println!("{}", serde_json::to_string(&summary).expect("summary serializes"));
            ExitCode::SUCCESS
        }
        Err(Failure::BadInput(msg)) => {
            println!("{}", serde_json::json!({ "error": msg }));
            ExitCode::from(2)
        }
        Err(Failure::Internal(msg)) => {
            println!("{}", serde_json::json!({ "error": msg }));
            ExitCode::from(1)
        }
    }
}

fn run(job: Job) -> Result<Summary, Failure> {
    let file = std::fs::File::open(&job.input)?;
    let mut reader = ImageReader::new(BufReader::new(file)).with_guessed_format()?;
    let input_format = match reader.format() {
        Some(f) => f,
        None => return bad("Not a supported image (JPEG, PNG, WebP, GIF, BMP or TIFF)."),
    };

    // Reject huge dimensions from the header alone, before the decoder allocates anything.
    let mut limits = Limits::default();
    let side = (job.max_pixels as f64).sqrt() as u32 * 4;
    limits.max_image_width = Some(side.max(1));
    limits.max_image_height = Some(side.max(1));
    limits.max_alloc = Some(job.max_pixels.saturating_mul(16).min(2 << 30));
    reader.limits(limits);

    let mut decoder = match reader.into_decoder() {
        Ok(d) => d,
        Err(e) => return bad(format!("The image could not be read: {e}")),
    };
    let (w, h) = decoder.dimensions();
    if (w as u64) * (h as u64) > job.max_pixels {
        return bad(format!("Image is {w}×{h}; the limit is {} megapixels.", job.max_pixels / 1_000_000));
    }
    let orientation = decoder.orientation().unwrap_or(image::metadata::Orientation::NoTransforms);
    let mut img = match DynamicImage::from_decoder(decoder) {
        Ok(i) => i,
        Err(e) => return bad(format!("The image could not be decoded: {e}")),
    };
    // Bake EXIF rotation into the pixels; re-encoding then drops all metadata (EXIF, GPS, ICC comments).
    img.apply_orientation(orientation);
    let (input_width, input_height) = (img.width(), img.height());

    for op in &job.ops {
        img = ops::apply(img, op, job.max_pixels)?;
    }

    let format = encode::resolve_format(&job.format, input_format)?;
    let (bytes, quality) = encode::encode(&img, format, job.quality.clamp(1, 100), job.lossy_png, job.target_bytes)?;
    std::fs::write(&job.output, &bytes)?;

    Ok(Summary {
        width: img.width(),
        height: img.height(),
        bytes: bytes.len() as u64,
        format: encode::name(format),
        quality,
        input_format: encode::name(input_format),
        input_width,
        input_height,
    })
}
