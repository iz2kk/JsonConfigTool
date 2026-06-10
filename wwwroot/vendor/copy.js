// copy.js
// Hàm copy prompt đã sửa
async function copyPrompt(button) {
  const text = button.getAttribute("data-content");

  if (!text) {
    alert("Không tìm thấy nội dung prompt để copy!");
    return;
  }

  try {
    await navigator.clipboard.writeText(text);

    // Hiệu ứng thành công
    const originalHTML = button.innerHTML;
    button.innerHTML = '<i class="fas fa-check"></i> Đã copy!';
    button.classList.remove("btn-outline-success");
    button.classList.add("btn-success");

    setTimeout(() => {
      button.innerHTML = originalHTML;
      button.classList.remove("btn-success");
      button.classList.add("btn-outline-success");
    }, 2000);
  } catch (err) {
    console.error("Clipboard error:", err);

    // Fallback nếu clipboard API không hoạt động (ví dụ: HTTP thay vì HTTPS)
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.style.position = "fixed";
    textarea.style.opacity = "0";
    document.body.appendChild(textarea);
    textarea.select();

    try {
      document.execCommand("copy");
      alert("Đã copy !");
    } catch (fallbackErr) {
      alert("Không thể copy tự động. Hãy chọn nội dung thủ công và Ctrl+C.");
    }

    document.body.removeChild(textarea);
  }
}
