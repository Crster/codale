namespace Codale.Browser;

/// <summary>
/// The element picker, run inside the page. While it is on, the element under the pointer gets a DevTools-style
/// bounding box (margin, border, padding and content shaded) with a label naming it and giving its size; a click
/// reports the element's CSS selector to the browser (which copies it) and Escape cancels. The overlay lives in a
/// closed shadow root so the page's CSS cannot restyle it. The page script posts through <c>window.chrome.webview</c>.
/// </summary>
internal static class PagePicker
{
    /// <summary>Turns the picker on in the current page. Starting it again replaces the previous picker.</summary>
    public const string Start = """
        (() => {
          if (window.__codalePick) window.__codalePick.stop();

          const post = message => window.chrome.webview.postMessage(message);

          // ---- Overlay ---------------------------------------------------------------
          const host = document.createElement('codale-pick');
          host.setAttribute('data-codale-pick', '');
          host.style.cssText = 'all:initial;position:fixed;inset:0;pointer-events:none;z-index:2147483647;';
          const root = host.attachShadow({ mode: 'closed' });

          // Styles go in through constructed stylesheets and the overlay is built node by node, so a page's
          // Content-Security-Policy (no inline styles) or Trusted Types (no innerHTML) cannot block the picker.
          const sheet = new CSSStyleSheet();
          sheet.replaceSync(`
            :host { all: initial; }
            div { position: fixed; box-sizing: border-box; pointer-events: none; }
            .layer { display: none; border-style: solid; border-width: 0; }
            .margin  { border-color: rgba(246, 178, 107, .55); }
            .border  { border-color: rgba(255, 229, 153, .65); }
            .padding { border-color: rgba(147, 196, 125, .55); background: rgba(111, 168, 220, .55); }
            .outline { display: none; outline: 1px solid #0ea5e9; outline-offset: 0; }
            .label {
              display: none; max-width: min(560px, calc(100vw - 16px)); padding: 4px 8px; border-radius: 4px;
              background: #1f2937; color: #e5e7eb; box-shadow: 0 2px 8px rgba(0,0,0,.35);
              font: 12px/1.4 ui-monospace, "Cascadia Mono", Consolas, monospace; white-space: nowrap;
              overflow: hidden; text-overflow: ellipsis;
            }
            .tag { color: #f0abfc; } .id { color: #fdba74; } .cls { color: #93c5fd; }
            .size { color: #9ca3af; margin-left: 8px; }
            .hint {
              left: 50%; top: 10px; transform: translateX(-50%); padding: 6px 12px; border-radius: 999px;
              background: rgba(17, 24, 39, .88); color: #f9fafb; box-shadow: 0 2px 10px rgba(0,0,0,.3);
              font: 12px/1.3 "Segoe UI", system-ui, sans-serif; white-space: nowrap; transition: opacity .15s;
            }
            .hint b { color: #7dd3fc; font-weight: 600; }`);
          root.adoptedStyleSheets = [sheet];

          const node = (tag, className, ...children) => {
            const el = document.createElement(tag);
            if (className) el.className = className;
            el.append(...children);
            return el;
          };
          const marginBox = node('div', 'layer margin');
          const borderBox = node('div', 'layer border');
          const paddingBox = node('div', 'layer padding');
          const outline = node('div', 'outline');
          const label = node('div', 'label');
          const hint = node('div', 'hint', 'Click an element to copy its ', node('b', '', 'CSS selector'), ' · ', node('b', '', 'Esc'), ' to cancel');
          root.append(marginBox, borderBox, paddingBox, outline, label, hint);
          document.documentElement.appendChild(host);

          // A crosshair over the whole page while picking; links and buttons would otherwise show their own cursors.
          const cursor = new CSSStyleSheet();
          cursor.replaceSync('*, *::before, *::after { cursor: crosshair !important; }');
          document.adoptedStyleSheets = [...document.adoptedStyleSheets, cursor];

          // ---- Selector ---------------------------------------------------------------
          const pathOf = element => {
            const attributes = ['data-testid', 'data-test', 'data-cy', 'name', 'aria-label'];
            const scope = element.getRootNode();
            const unique = selector => {
              try { const found = scope.querySelectorAll(selector); return found.length === 1 && found[0] === element; }
              catch { return false; }
            };
            if (element.id) {
              const byId = '#' + CSS.escape(element.id);
              if (unique(byId)) return byId;
            }
            for (const attribute of attributes) {
              const value = element.getAttribute(attribute);
              if (value && value.length <= 160) {
                const selector = element.localName + '[' + attribute + '=' + JSON.stringify(value) + ']';
                if (unique(selector)) return selector;
              }
            }
            const parts = [];
            for (let node = element; node && node.nodeType === 1; node = node.parentElement) {
              if (node !== element && node.id) {
                const byAncestorId = '#' + CSS.escape(node.id);
                if (unique(byAncestorId)) { parts.unshift(byAncestorId); break; }
              }
              let part = CSS.escape(node.localName);
              if (node.parentElement) {
                const siblings = [...node.parentElement.children].filter(child => child.localName === node.localName);
                if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')';
              }
              parts.unshift(part);
            }
            return parts.join(' > ');
          };

          // ---- Drawing ----------------------------------------------------------------
          const px = value => parseFloat(value) || 0;
          const place = (el, left, top, width, height, edges) => {
            Object.assign(el.style, {
              display: 'block', left: left + 'px', top: top + 'px',
              width: Math.max(0, width) + 'px', height: Math.max(0, height) + 'px',
            });
            if (edges) el.style.borderWidth = edges.map(e => Math.max(0, e) + 'px').join(' ');
          };

          let current = null;
          const draw = () => {
            const element = current;
            if (!element || !element.isConnected) {
              [marginBox, borderBox, paddingBox, outline, label].forEach(el => el.style.display = 'none');
              return;
            }
            const box = element.getBoundingClientRect();
            const s = getComputedStyle(element);
            const m = [px(s.marginTop), px(s.marginRight), px(s.marginBottom), px(s.marginLeft)];
            const b = [px(s.borderTopWidth), px(s.borderRightWidth), px(s.borderBottomWidth), px(s.borderLeftWidth)];
            const p = [px(s.paddingTop), px(s.paddingRight), px(s.paddingBottom), px(s.paddingLeft)];

            place(marginBox, box.left - m[3], box.top - m[0], box.width + m[1] + m[3], box.height + m[0] + m[2], m);
            place(borderBox, box.left, box.top, box.width, box.height, b);
            place(paddingBox, box.left + b[3], box.top + b[0], box.width - b[1] - b[3], box.height - b[0] - b[2], p);
            place(outline, box.left, box.top, box.width, box.height);

            const size = Math.round(box.width * 100) / 100 + ' × ' + Math.round(box.height * 100) / 100;
            const parts = [node('span', 'tag', element.localName)];
            if (element.id) parts.push(node('span', 'id', '#' + element.id));
            [...element.classList].slice(0, 3).forEach(c => parts.push(node('span', 'cls', '.' + c)));
            if (element.classList.length > 3) parts.push(node('span', 'cls', '…'));
            parts.push(node('span', 'size', size));
            label.replaceChildren(...parts);
            label.style.display = 'block';

            // The label sits above the box, below it when there is no room, and inside the viewport either way.
            const tip = label.getBoundingClientRect();
            const top = box.top - m[0] - tip.height - 6 >= 4 ? box.top - m[0] - tip.height - 6
              : Math.min(box.bottom + m[2] + 6, innerHeight - tip.height - 4);
            label.style.left = Math.max(4, Math.min(box.left, innerWidth - tip.width - 4)) + 'px';
            label.style.top = Math.max(4, top) + 'px';

            // The hint steps aside while the pointer is over the top of the page.
            hint.style.opacity = box.top < 48 && box.left < innerWidth / 2 + 200 && box.right > innerWidth / 2 - 200 ? '0' : '1';
          };

          let frame = 0;
          const schedule = () => { if (!frame) frame = requestAnimationFrame(() => { frame = 0; draw(); }); };

          // ---- Events -----------------------------------------------------------------
          const targetOf = event => event.composedPath().find(node => node instanceof Element && node !== host) || null;
          const swallow = event => { event.preventDefault(); event.stopImmediatePropagation(); };

          const hover = event => {
            const element = targetOf(event);
            if (element === current) return;
            current = element;
            schedule();
          };

          const click = event => {
            const element = targetOf(event);
            swallow(event);
            stop();
            if (element) post({ type: 'codale-pick', selector: pathOf(element) });
            else post({ type: 'codale-pick-cancel' });
          };

          const key = event => {
            if (event.key !== 'Escape') return;
            swallow(event);
            stop();
            post({ type: 'codale-pick-cancel' });
          };

          // Presses are swallowed too, so menus and drag handlers in the page do not react to the pick.
          const press = event => { if (event.button === 0) swallow(event); };

          const listeners = [
            ['pointermove', hover], ['pointerover', hover], ['click', click], ['keydown', key],
            ['pointerdown', press], ['mousedown', press], ['pointerup', press], ['mouseup', press],
          ];
          const stop = () => {
            listeners.forEach(([name, handler]) => document.removeEventListener(name, handler, true));
            removeEventListener('scroll', schedule, true);
            removeEventListener('resize', schedule, true);
            if (frame) cancelAnimationFrame(frame);
            host.remove();
            document.adoptedStyleSheets = document.adoptedStyleSheets.filter(s => s !== cursor);
            delete window.__codalePick;
          };

          window.__codalePick = { stop };
          listeners.forEach(([name, handler]) => document.addEventListener(name, handler, true));
          addEventListener('scroll', schedule, true);
          addEventListener('resize', schedule, true);
        })();
        """;

    /// <summary>Turns the picker off in the current page, if it is on.</summary>
    public const string Stop = "window.__codalePick && window.__codalePick.stop();";
}
