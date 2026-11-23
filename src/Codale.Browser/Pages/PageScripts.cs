namespace Codale.Browser;

/// <summary>
/// JavaScript the browser runs in pages. <see cref="Init"/> is added to every document before it loads:
/// it reports submitted logins to the host, fills a saved login when the host sends one, and notes each
/// passkey ceremony (navigator.credentials.create/get) without changing what the page gets back.
/// Only the top document takes part. <see cref="Pick"/> is run on demand for the element picker.
/// </summary>
internal static class PageScripts
{
    public const string Init = """
        (() => {
          if (window.top !== window || window.__codaleInstalled) return;
          window.__codaleInstalled = true;
          const post = message => { try { window.chrome.webview.postMessage(message); } catch { } };
          const visible = el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
          const textLike = el => ['text', 'email', 'tel', ''].includes((el.type || '').toLowerCase()) && !el.disabled && !el.readOnly;

          // The username is the closest text-like field before the password field, preferring one that looks like a login name.
          const usernameField = (scope, password) => {
            const before = [...scope.querySelectorAll('input')].filter(i => textLike(i) && (i.compareDocumentPosition(password) & Node.DOCUMENT_POSITION_FOLLOWING));
            const named = before.filter(i => /user|email|login|account|identifier|name/i.test(`${i.autocomplete} ${i.name} ${i.id}`));
            return (named.length ? named : before).at(-1) ?? null;
          };

          let lastKey = '', lastAt = 0;
          const submitted = scope => {
            const password = [...scope.querySelectorAll('input[type=password]')].find(i => i.value);
            if (!password) return;
            const username = usernameField(scope, password)?.value ?? '';
            const key = location.origin + '\n' + username + '\n' + password.value;
            const now = Date.now();
            if (key === lastKey && now - lastAt < 2000) return;
            lastKey = key; lastAt = now;
            post({ type: 'codale-credentials', origin: location.origin, username, password: password.value });
          };
          document.addEventListener('submit', e => submitted(e.target instanceof HTMLFormElement ? e.target : document), true);
          document.addEventListener('click', e => {
            const button = e.target?.closest?.('button, input[type=submit], input[type=button], [role=button]');
            if (button && document.querySelector('input[type=password]')) setTimeout(() => submitted(button.form ?? document), 0);
          }, true);

          // Filling: a saved login is put in the first empty, visible password field; pages that render their form late get a few retries.
          const setValue = (el, value) => {
            const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value')?.set;
            if (setter) setter.call(el, value); else el.value = value;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
          };
          const fill = (username, password) => {
            const field = [...document.querySelectorAll('input[type=password]')].find(i => visible(i) && !i.value);
            if (!field) return false;
            const user = usernameField(field.form ?? document, field);
            if (user && !user.value) setValue(user, username);
            setValue(field, password);
            return true;
          };
          let attempts = 0;
          const tryFill = message => {
            if (fill(message.username, message.password) || ++attempts > 20) return;
            setTimeout(() => tryFill(message), 300);
          };
          window.chrome.webview.addEventListener('message', e => {
            if (e.data?.type === 'codale-fill') { attempts = 0; tryFill(e.data); }
          });

          // Passkeys: the call goes ahead untouched; the host only learns that the site asked for one.
          const credentials = navigator.credentials;
          if (credentials) {
            for (const op of ['create', 'get']) {
              const original = credentials[op];
              if (typeof original !== 'function') continue;
              credentials[op] = function (...args) {
                post({ type: 'codale-passkey', op, origin: location.origin });
                return original.apply(this, args);
              };
            }
          }
        })();
        """;

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
