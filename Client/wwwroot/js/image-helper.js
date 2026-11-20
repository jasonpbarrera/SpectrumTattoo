window.spectrumImages = {
  processFiles: async function (inputId, maxFiles, maxDim, quality) {
    const input = document.getElementById(inputId);
    if (!input || !input.files || input.files.length === 0) {
      return [];
    }

    const limit = Math.max(1, maxFiles || 3);
    const files = Array.from(input.files).slice(0, limit);
    const results = [];

    for (const file of files) {
      if (!file.type || !file.type.startsWith("image/")) continue;
      try {
        const dataUrl = await convertToWebP(file, maxDim || 960, quality || 0.78);
        if (dataUrl) results.push(dataUrl);
      } catch (err) {
        console.warn("Skipping reference image:", err);
      }
    }

    input.value = "";
    return results;
  }
};

function convertToWebP(file, maxDim, quality) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("Failed to read file"));
    reader.onload = () => {
      const img = new Image();
      img.onerror = () => reject(new Error("Failed to decode image"));
      img.onload = () => {
        const encode = (dim, q) => {
          let w = img.naturalWidth || img.width;
          let h = img.naturalHeight || img.height;
          if (w > dim || h > dim) {
            if (w >= h) {
              h = Math.round((h * dim) / w);
              w = dim;
            } else {
              w = Math.round((w * dim) / h);
              h = dim;
            }
          }
          const canvas = document.createElement("canvas");
          canvas.width = Math.max(1, w);
          canvas.height = Math.max(1, h);
          const ctx = canvas.getContext("2d");
          ctx.drawImage(img, 0, 0, canvas.width, canvas.height);

          let out = canvas.toDataURL("image/webp", q);
          if (!out.startsWith("data:image/webp")) {
            out = canvas.toDataURL("image/jpeg", q);
          }
          return out;
        };

        let out = encode(maxDim, quality);
        if (out.length > 280000) {
          out = encode(Math.min(760, maxDim), 0.62);
        }
        resolve(out);
      };
      img.src = reader.result;
    };
    reader.readAsDataURL(file);
  });
}
