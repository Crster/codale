namespace Codale.Browser;

/// <summary>JavaScript the browser runs in pages. <see cref="Pick"/> is run on demand for the element picker.</summary>
internal static class PageScripts
{
    /// <summary>Element picker: hover outlines an element, a click copies its selector path (Esc cancels).</summary>
    public const string Pick = """
        (() => {
          window.__codalePickCleanup?.();
          const overlay = document.createElement('div');
          overlay.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;box-sizing:border-box;border:2px solid #3b82f6;background:rgba(59,130,246,.15);display:none;';
          document.documentElement.appendChild(overlay);
          const cleanup = () => {
            document.removeEventListener('click', handler, true);
            document.removeEventListener('pointermove', hover, true);
            document.removeEventListener('keydown', escape, true);
            overlay.remove();
            delete window.__codalePickCleanup;
          };
          const hover = e => {
            const element = e.composedPath().find(x => x instanceof Element && x !== overlay);
            if (!element) return;
            const r = element.getBoundingClientRect();
            Object.assign(overlay.style, {display:'block', left:r.left+'px', top:r.top+'px', width:r.width+'px', height:r.height+'px'});
          };
          const escape = e => {
            if (e.key !== 'Escape') return;
            e.preventDefault(); e.stopImmediatePropagation(); cleanup();
            window.chrome.webview.postMessage({type:'codale-pick-cancel'});
          };
          const selectorFor = (element, root) => {
            const unique = selector => {
              try { return root.querySelectorAll(selector).length === 1 && root.querySelector(selector) === element; }
              catch { return false; }
            };
            const candidates = [];
            if (element.id) candidates.push('#' + CSS.escape(element.id));
            for (const attr of ['data-testid', 'data-test', 'data-cy', 'name', 'aria-label']) {
              const value = element.getAttribute(attr);
              if (value && value.length <= 160) candidates.push(element.localName + '[' + attr + '=' + CSS.escape(value) + ']');
            }
            const alternatives = candidates.filter(unique);
            let node = element, parts = [];
            while (node && node.nodeType === 1) {
              let part = CSS.escape(node.localName);
              if (node.id) {
                const id = '#' + CSS.escape(node.id);
                try { if (root.querySelectorAll(id).length === 1) { parts.unshift(id); break; } } catch { }
              }
              const parent = node.parentElement || (node.parentNode instanceof ShadowRoot ? node.parentNode : null);
              const siblings = parent ? [...parent.children].filter(x => x.localName === node.localName) : [];
              if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')';
              parts.unshift(part);
              node = node.parentElement;
            }
            const fallback = parts.join(' > ');
            if (unique(fallback) && !alternatives.includes(fallback)) alternatives.push(fallback);
            return alternatives;
          };
          const handler = e => {
            e.preventDefault(); e.stopImmediatePropagation();
            const element = e.composedPath().find(x => x instanceof Element) || e.target;
            cleanup();
            const root = element.getRootNode();
            const selectors = selectorFor(element, root);
            const shadowHosts = [];
            for (let current = root; current instanceof ShadowRoot; current = current.host.getRootNode()) {
              const parent = current.host.getRootNode();
              shadowHosts.unshift(selectorFor(current.host, parent)[0] || current.host.localName);
            }
            window.chrome.webview.postMessage({type:'codale-pick', selectors, shadowHosts});
          };
          window.__codalePickCleanup = cleanup;
          document.addEventListener('click', handler, true);
          document.addEventListener('pointermove', hover, true);
          document.addEventListener('keydown', escape, true);
        })()
        """;
}
