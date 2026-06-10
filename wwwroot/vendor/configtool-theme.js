window.configToolTheme = window.configToolTheme || {
  applyCssText: function (cssText, shellClass) {
    const css = cssText || '';
    let live = document.getElementById('configtool-live-theme');
    if (!live) {
      live = document.createElement('style');
      live.id = 'configtool-live-theme';
      document.head.appendChild(live);
    }
    live.textContent = css;

    const server = document.getElementById('configtool-server-theme');
    if (server) server.textContent = css;

    const shell = document.querySelector('.ct-shell');
    const targets = [document.documentElement, document.body, shell].filter(Boolean);
    for (const target of targets) {
      const removeList = [];
      for (const cls of target.classList) {
        if (String(cls).startsWith('theme-layout-') || String(cls).startsWith('theme-nav-')) {
          removeList.push(cls);
        }
      }
      for (const cls of removeList) target.classList.remove(cls);
      if (shellClass) {
        for (const part of String(shellClass).split(/\s+/)) {
          if (part && part.startsWith('theme-')) target.classList.add(part);
        }
      }
    }

    try {
      localStorage.setItem('configtool:lastThemeCss', css);
      localStorage.setItem('configtool:lastThemeShellClass', shellClass || '');
    } catch { }

    window.dispatchEvent(new CustomEvent('configtool-theme-applied', {
      detail: { shellClass: shellClass || '', length: css.length }
    }));
  },
  downloadText: function (filename, content, mimeType) {
    const blob = new Blob([content || ''], { type: mimeType || 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename || 'theme.css';
    document.body.appendChild(a);
    a.click();
    setTimeout(function () {
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    }, 0);
  },
  copyText: async function (text) {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text || '');
      return;
    }
    const textarea = document.createElement('textarea');
    textarea.value = text || '';
    textarea.style.position = 'fixed';
    textarea.style.left = '-9999px';
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    document.execCommand('copy');
    document.body.removeChild(textarea);
  }
};
