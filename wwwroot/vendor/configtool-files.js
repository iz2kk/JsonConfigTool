window.configToolFiles = window.configToolFiles || {
  downloadTextFile: function (fileName, content, mimeType) {
    const blob = new Blob([content || ""], { type: mimeType || "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName || "export.sql";
    anchor.style.display = "none";
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  },
  copyText: async function (content) {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(content || "");
      return;
    }

    const textarea = document.createElement("textarea");
    textarea.value = content || "";
    textarea.style.position = "fixed";
    textarea.style.left = "-9999px";
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    document.execCommand("copy");
    document.body.removeChild(textarea);
  }
};
