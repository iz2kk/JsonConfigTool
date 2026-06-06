function spinner(el, state = true) {
  const $btn = $(el);
  const $icon = $btn.find("i");

  if (!$icon.length) return;

  if (state) {
    $btn.data("old-class", $icon.attr("class"));
    $icon.attr("class", "fas fa-spinner fa-spin");
    $btn.prop("disabled", true);
  } else {
    const old = $btn.data("old-class");
    if (old) $icon.attr("class", old);
    else $icon.removeClass("fa-spin");

    $btn.prop("disabled", false);
  }
}
function timeAgo(inputTime) {
  const now = new Date();
  const time = new Date(inputTime);
  const diff = Math.floor((now - time) / 1000); // tính bằng giây

  if (diff < 5) return "Vừa xong";
  if (diff < 60) return `${diff} giây trước`;

  const minutes = Math.floor(diff / 60);
  if (minutes < 60) return `${minutes} phút trước`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} giờ trước`;

  const days = Math.floor(hours / 24);
  if (days < 7) return `${days} ngày trước`;

  const weeks = Math.floor(days / 7);
  if (weeks < 4) return `${weeks} tuần trước`;

  const months = Math.floor(days / 30);
  if (months < 12) return `${months} tháng trước`;

  const years = Math.floor(days / 365);
  return `${years} năm trước`;
}
function renderStatusBadge(status) {
  const value = String(status || "")
    .trim()
    .toLowerCase();

  const map = {
    banned: {
      text: "Banned",
      className: "badge bg-danger",
    },
    pending: {
      text: "Pending",
      className: "badge bg-warning text-dark",
    },
    active: {
      text: "Active",
      className: "badge bg-success",
    },
  };

  const item = map[value] || {
    text: status || "Unknown",
    className: "badge bg-secondary",
  };

  return `<span class="${item.className}">${item.text}</span>`;
}
function removeHtmlAndCSS(htmlContent) {
  var cleanedContent = htmlContent
    .replace(/<style[^>]*>[\s\S]*?<\/style>/gi, "") // Loại bỏ thẻ <style>
    .replace(/<script[^>]*>[\s\S]*?<\/script>/gi, "") // Loại bỏ thẻ <script>
    .replace(/<link[^>]*>/gi, "") // Loại bỏ thẻ <link>
    .replace(/<\/?[^>]+(>|$)/g, ""); // Loại bỏ tất cả các thẻ HTML còn lại

  return cleanedContent;
}
const txt = {
  stripHtml: function (str) {
    return str
      .replace(/<style[^>]*>[\s\S]*?<\/style>/gi, "")
      .replace(/<script[^>]*>[\s\S]*?<\/script>/gi, "")
      .replace(/<[^>]+>/g, "")
      .replace(/\s+/g, " ")
      .trim();
  },

  // Bạn có thể thêm các hàm khác vào đây
  truncate: function (str, limit) {
    return str.length > limit ? str.substring(0, limit) + "..." : str;
  },
  stripAll: function (str) {
    if (!str) return "";

    return (
      str
        // 1. Xóa toàn bộ nội dung nằm trong thẻ <style> hoặc <script> (nếu còn thẻ)
        .replace(/<(style|script)[^>]*>[\s\S]*?<\/\1>/gi, "")

        // 2. Xóa các khối CSS bung ra ngoài: nhận diện bằng { ... }
        // Regex này xóa từ tên class/id cho đến hết dấu đóng ngoặc nhọn
        .replace(/[^{}]+\{[^{}]*\}/g, "")

        // 3. Xóa các dòng bắt đầu bằng @ (như @media, @font-face)
        .replace(/@[^;{]+(?=\{)/g, "")

        // 4. Xóa các thẻ HTML còn sót lại
        .replace(/<[^>]+>/g, "")

        // 5. Xóa các dấu ngoặc nhọn lẻ loi (nếu có)
        .replace(/[{} ]+/g, " ")

        // 6. Dọn dẹp khoảng trắng và xuống dòng
        .replace(/\s\s+/g, " ")
        .trim()
    );
  },
  stripClean: function (str) {
    if (!str) return "";

    return (
      str
        // 1. Xóa toàn bộ nội dung trong thẻ <style>/<script> và các thẻ HTML
        .replace(/<(style|script)[^>]*>[\s\S]*?<\/\1>/gi, "")
        .replace(/<[^>]+>/g, "")

        // 2. Xóa các khối CSS có ngoặc nhọn { ... } (kể cả lồng nhau)
        .replace(/[^{}]*\{[^{}]*\}/g, "")

        // 3. XÓA SNIPPET CSS VỤN (Cấu trúc property: value;)
        // Regex này tìm các cụm như font-family: ...; hoặc src: url(...);
        .replace(/[a-zA-Z-]+\s*:\s*[^;]+(;|$)/g, "")

        // 4. Xóa các hàm CSS đặc thù như url(...), format(...)
        .replace(/\b(url|format|calc|rgba?|hsla?)\s*\([^)]*\)/gi, "")

        // 5. Xóa các chỉ thị bắt đầu bằng dấu @ (media, font-face,...)
        .replace(/@[^ ]+/g, "")

        // 6. Xóa các ký tự rác CSS còn sót lại (dấu chấm phẩy, ngoặc kép dư)
        .replace(/[;\"\'\(\)]/g, "")

        // 7. Dọn dẹp khoảng trắng
        .replace(/\s\s+/g, " ")
        .trim()
    );
  },
};

// Cách dùng:
// const cleanText = TextLib.stripHtml(htmlInput);
