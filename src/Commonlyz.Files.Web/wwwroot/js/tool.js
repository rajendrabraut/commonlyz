// Commonlyz Files tool page: pick files, collect options, upload to the API, show results.
// No frameworks and no inline script (the page runs under a strict Content-Security-Policy).
(() => {
  "use strict";

  const page = document.querySelector(".tool-page");
  if (!page) return;

  const api = document.body.dataset.api;
  const endpoint = page.dataset.endpoint;
  const panel = page.dataset.panel;
  const preset = JSON.parse(page.dataset.preset || "{}");
  const MAX_BYTES = 50 * 1024 * 1024;

  const $ = (id) => document.getElementById(id);
  const input = $("file-input");
  const dropzone = $("dropzone");
  const list = $("file-list");
  const form = $("options");
  const run = $("run");
  const progress = $("progress");
  const errorBox = $("error");
  const results = $("results");
  const resultList = $("result-list");
  const downloadAll = $("download-all");

  let files = [];

  // ---------------------------------------------------------------- option values

  /** Reads every [data-opt] control into a plain object (radios: the checked one). */
  function readOptions() {
    const values = {};
    form.querySelectorAll("[data-opt]").forEach((el) => {
      const key = el.dataset.opt;
      if (el.type === "radio") { if (el.checked) values[key] = el.value; }
      else if (el.type === "checkbox") values[key] = el.checked;
      else if (el.type === "number" || el.type === "range") values[key] = el.value === "" ? null : Number(el.value);
      else values[key] = el.value;
    });
    return values;
  }

  const ratio = (text) => { const [w, h] = text.split(":").map(Number); return { ratioWidth: w, ratioHeight: h }; };
  const encode = (v) => ({ format: v.format, quality: v.quality });

  /** Panel-specific translation of form values into the API's options JSON. */
  const builders = {
    "none": () => ({}),
    "merge": (v) => ({ mergeIntoOne: v.mergeIntoOne }),
    "pdf-to-image": (v) => ({ dpi: v.dpi, quality: v.quality, pages: v.pages || null }),
    "image-to-pdf": (v) => ({ pageSize: v.pageSize, marginPoints: v.marginPoints, mergeIntoOne: v.mergeIntoOne }),
    "image-format": (v) => ({ to: v.to, quality: v.quality }),
    "compress": (v) => ({
      format: v.format, lossyPng: v.lossyPng, operations: [],
      ...(v.mode === "size" ? { targetKb: v.targetKb, format: v.format === "same" ? "jpeg" : v.format } : { quality: v.quality }),
    }),
    "resize": (v) => ({
      ...encode(v),
      operations: [v.mode === "percent"
        ? { type: "scale", percent: v.percent }
        : { type: "resize", width: v.width, height: v.height, fit: v.fit, allowUpscale: v.allowUpscale }],
    }),
    "crop": (v) => ({ ...encode(v), operations: [{ type: "crop", x: v.x, y: v.y, width: v.width, height: v.height }] }),
    "ratio": (v) => ({
      ...encode(v),
      operations: [v.method === "pad" ? { type: "pad_ratio", ...ratio(v.ratio), color: v.color } : { type: "crop_ratio", ...ratio(v.ratio) }],
    }),
    "rotate": (v) => {
      const ops = [];
      if (v.degrees) ops.push({ type: "rotate", degrees: v.degrees });
      if (v.flipH) ops.push({ type: "flip", axis: "horizontal" });
      if (v.flipV) ops.push({ type: "flip", axis: "vertical" });
      return { ...encode(v), operations: ops };
    },
    "enhance": (v) => {
      const ops = [];
      if (v.denoise > 0) ops.push({ type: "denoise", strength: v.denoise });
      if (v.auto) ops.push({ type: "auto_enhance" });
      if (v.brightness || v.contrast || v.saturation) ops.push({ type: "adjust", brightness: v.brightness, contrast: v.contrast, saturation: v.saturation });
      if (v.sharpen > 0) ops.push({ type: "sharpen", amount: v.sharpen, radius: 1.2 });
      return { ...encode(v), operations: ops };
    },
    "upscale": (v) => ({ scale: v.scale, format: v.format }),
    "degrade": (v) => {
      const ops = [];
      if (v.percent < 100) ops.push({ type: "scale", percent: v.percent });
      if (v.blur > 0) ops.push({ type: "blur", sigma: v.blur });
      if (v.grayscale) ops.push({ type: "grayscale" });
      return { format: "jpeg", quality: v.quality, operations: ops };
    },
  };

  // Show/hide fields that depend on a choice (data-show="mode=size").
  function syncVisibility() {
    const v = readOptions();
    form.querySelectorAll("[data-show]").forEach((el) => {
      const [key, value] = el.dataset.show.split("=");
      el.hidden = String(v[key]) !== value;
    });
    form.querySelectorAll("output[data-for]").forEach((out) => {
      const value = v[out.dataset.for];
      if (value !== undefined && value !== null) out.textContent = value;
    });
  }
  form.addEventListener("input", syncVisibility);
  syncVisibility();

  // ---------------------------------------------------------------- file selection

  function setFiles(selected) {
    const accepted = [];
    const rejected = [];
    for (const f of selected) (f.size > MAX_BYTES ? rejected : accepted).push(f);
    files = input.multiple ? files.concat(accepted) : accepted.slice(0, 1);
    showError(rejected.length ? `${rejected.map((f) => f.name).join(", ")}: larger than 50 MB.` : "");
    renderFiles();
    if (panel === "crop" && files[0]) cropper.load(files[0]);
  }

  function renderFiles() {
    list.replaceChildren(...files.map((f, i) => {
      const li = document.createElement("li");
      const name = document.createElement("span");
      name.textContent = f.name;
      const size = document.createElement("small");
      size.textContent = formatBytes(f.size);
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "link";
      remove.textContent = "Remove";
      remove.addEventListener("click", () => { files.splice(i, 1); renderFiles(); });
      li.append(name, size, remove);
      return li;
    }));
    run.disabled = files.length === 0;
  }

  input.addEventListener("change", () => { setFiles([...input.files]); input.value = ""; });
  ["dragenter", "dragover"].forEach((t) => dropzone.addEventListener(t, (e) => { e.preventDefault(); dropzone.classList.add("over"); }));
  ["dragleave", "drop"].forEach((t) => dropzone.addEventListener(t, () => dropzone.classList.remove("over")));
  dropzone.addEventListener("drop", (e) => { e.preventDefault(); setFiles([...e.dataTransfer.files]); });

  // ---------------------------------------------------------------- upload & results

  form.addEventListener("submit", (e) => {
    e.preventDefault();
    if (!files.length) return;
    const options = { ...builders[panel](readOptions()), ...preset };
    const body = new FormData();
    files.forEach((f) => body.append("files", f, f.name));
    body.append("options", JSON.stringify(options));

    showError("");
    results.hidden = true;
    run.disabled = true;
    progress.hidden = false;
    setProgress(0, "Uploading…");

    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${api}/v1/${endpoint}`);
    xhr.responseType = "json";
    xhr.upload.addEventListener("progress", (ev) => {
      if (ev.lengthComputable) setProgress(ev.loaded / ev.total, ev.loaded < ev.total ? "Uploading…" : "Processing…");
    });
    xhr.addEventListener("load", () => {
      progress.hidden = true;
      run.disabled = false;
      if (xhr.status === 200 && xhr.response) showResults(xhr.response);
      else if (xhr.status === 429) showError("Too many requests. Please wait a minute and try again.");
      else showError((xhr.response && (xhr.response.detail || xhr.response.title)) || `Request failed (${xhr.status}).`);
    });
    xhr.addEventListener("error", () => {
      progress.hidden = true;
      run.disabled = false;
      showError("Could not reach the server. Check your connection and try again.");
    });
    xhr.send(body);
  });

  function showResults(job) {
    resultList.replaceChildren(...job.files.map((f) => {
      const li = document.createElement("li");
      const url = `${api}/v1/files/${encodeURIComponent(f.id)}`;
      if (f.contentType.startsWith("image/")) {
        const img = document.createElement("img");
        img.src = `${url}?inline=true`;
        img.alt = "";
        img.loading = "lazy";
        li.append(img);
      } else {
        const icon = document.createElement("span");
        icon.className = "file-icon";
        icon.textContent = f.name.split(".").pop().toUpperCase();
        li.append(icon);
      }
      const info = document.createElement("div");
      info.className = "info";
      const name = document.createElement("strong");
      name.textContent = f.name;
      const meta = document.createElement("small");
      const dims = f.width ? `${f.width}×${f.height} · ` : "";
      const saved = f.sourceSize ? ` (${savings(f.sourceSize, f.size)})` : "";
      meta.textContent = `${dims}${formatBytes(f.size)}${saved}`;
      info.append(name, meta);
      const link = document.createElement("a");
      link.className = "button";
      link.href = url;
      link.textContent = "Download";
      li.append(info, link);
      return li;
    }));
    downloadAll.hidden = job.files.length < 2;
    downloadAll.href = `${api}/v1/jobs/${encodeURIComponent(job.jobId)}/zip`;
    results.hidden = false;
    results.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  function savings(before, after) {
    if (after >= before) return `+${Math.round(((after - before) / before) * 100)}%`;
    return `${Math.round(((before - after) / before) * 100)}% smaller`;
  }

  function setProgress(fraction, label) {
    progress.querySelector(".bar").style.width = `${Math.round(fraction * 100)}%`;
    progress.querySelector(".label").textContent = label;
  }

  function showError(message) {
    errorBox.textContent = message;
    errorBox.hidden = !message;
  }

  function formatBytes(n) {
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / 1024 / 1024).toFixed(1)} MB`;
  }

  // ---------------------------------------------------------------- crop UI

  const cropper = (() => {
    const wrap = $("cropper");
    if (!wrap) return { load() {} };
    const img = $("crop-image");
    const box = $("crop-box");
    const fields = { x: $("crop-x"), y: $("crop-y"), w: $("crop-w"), h: $("crop-h") };
    const lock = $("crop-lock");
    let natural = { w: 1, h: 1 };
    let rect = { x: 0, y: 0, w: 1, h: 1 }; // in natural (file) pixels
    let objectUrl = null;

    const scale = () => img.clientWidth / natural.w;
    const ratioValue = () => (lock.value === "free" ? null : lock.value.split(":").map(Number).reduce((a, b) => a / b));

    function clamp() {
      rect.w = Math.max(1, Math.min(rect.w, natural.w));
      rect.h = Math.max(1, Math.min(rect.h, natural.h));
      rect.x = Math.max(0, Math.min(rect.x, natural.w - rect.w));
      rect.y = Math.max(0, Math.min(rect.y, natural.h - rect.h));
    }

    function draw() {
      clamp();
      const s = scale();
      Object.assign(box.style, { left: `${rect.x * s}px`, top: `${rect.y * s}px`, width: `${rect.w * s}px`, height: `${rect.h * s}px` });
      fields.x.value = Math.round(rect.x);
      fields.y.value = Math.round(rect.y);
      fields.w.value = Math.round(rect.w);
      fields.h.value = Math.round(rect.h);
    }

    function applyLock() {
      const r = ratioValue();
      if (!r) return;
      if (rect.w / rect.h > r) rect.w = rect.h * r; else rect.h = rect.w / r;
      draw();
    }

    let drag = null;
    box.addEventListener("pointerdown", (e) => {
      e.preventDefault();
      box.setPointerCapture(e.pointerId);
      drag = { mode: e.target.dataset.handle ? "resize" : "move", x: e.clientX, y: e.clientY, start: { ...rect } };
    });
    box.addEventListener("pointermove", (e) => {
      if (!drag) return;
      const s = scale();
      const dx = (e.clientX - drag.x) / s;
      const dy = (e.clientY - drag.y) / s;
      if (drag.mode === "move") {
        rect.x = drag.start.x + dx;
        rect.y = drag.start.y + dy;
      } else {
        rect.w = drag.start.w + dx;
        rect.h = ratioValue() ? rect.w / ratioValue() : drag.start.h + dy;
      }
      draw();
    });
    box.addEventListener("pointerup", () => { drag = null; });
    lock.addEventListener("change", applyLock);
    Object.entries(fields).forEach(([k, el]) => el.addEventListener("change", () => { rect[k] = Number(el.value) || 0; draw(); }));
    window.addEventListener("resize", draw);

    return {
      load(file) {
        if (objectUrl) URL.revokeObjectURL(objectUrl);
        objectUrl = URL.createObjectURL(file);
        img.onload = () => {
          natural = { w: img.naturalWidth, h: img.naturalHeight };
          rect = { x: natural.w * 0.1, y: natural.h * 0.1, w: natural.w * 0.8, h: natural.h * 0.8 };
          wrap.hidden = false;
          applyLock();
          draw();
        };
        img.src = objectUrl;
      },
    };
  })();
})();
