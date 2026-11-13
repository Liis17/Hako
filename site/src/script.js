(() => {
  const menuButton = document.querySelector('.nav-toggle');
  const menu = document.querySelector('#nav-links');

  if (menuButton && menu) {
    const closeMenu = () => {
      menu.classList.remove('open');
      menuButton.setAttribute('aria-expanded', 'false');
      menuButton.setAttribute('aria-label', 'Открыть меню');
    };

    menuButton.addEventListener('click', () => {
      const open = menu.classList.toggle('open');
      menuButton.setAttribute('aria-expanded', String(open));
      menuButton.setAttribute('aria-label', open ? 'Закрыть меню' : 'Открыть меню');
    });
    menu.querySelectorAll('a').forEach(link => link.addEventListener('click', closeMenu));
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') closeMenu();
    });
  }

  const tabs = [...document.querySelectorAll('.preview-tab')];
  const panels = [...document.querySelectorAll('.app-panel')];
  function activateTab(target, moveFocus = false) {
    tabs.forEach(tab => {
      const active = tab === target;
      tab.classList.toggle('active', active);
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
    });
    panels.forEach(panel => {
      const active = panel.id === `panel-${target.dataset.tab}`;
      panel.hidden = !active;
      panel.classList.toggle('active', active);
    });
    if (moveFocus) target.focus();
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => activateTab(tab));
    tab.addEventListener('keydown', event => {
      let next = index;
      if (event.key === 'ArrowDown' || event.key === 'ArrowRight') next = (index + 1) % tabs.length;
      else if (event.key === 'ArrowUp' || event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
      else if (event.key === 'Home') next = 0;
      else if (event.key === 'End') next = tabs.length - 1;
      else return;
      event.preventDefault();
      activateTab(tabs[next], true);
    });
  });

  const parseVersion = (tag, prefix) => {
    const match = new RegExp(`^${prefix}-v(\\d+)\\.(\\d+)\\.(\\d+)$`).exec(tag || '');
    return match ? match.slice(1).map(Number) : null;
  };
  const newer = (a, b) => {
    for (let i = 0; i < 3; i += 1) if (a[i] !== b[i]) return a[i] > b[i];
    return false;
  };
  const dateFormat = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
  const sizeLabel = bytes => `${(bytes / 1048576).toFixed(0)} МБ`;

  function latestRelease(releases, prefix) {
    let best = null;
    for (const release of releases) {
      if (release.draft || release.prerelease) continue;
      const version = parseVersion(release.tag_name, prefix);
      if (version && (!best || newer(version, best.version))) best = { release, version };
    }
    return best;
  }

  function fillPlatform(platform, found, assetNames) {
    const card = document.querySelector(`.download-card[data-platform="${platform}"]`);
    if (!card || !found) return;
    const { release, version } = found;
    const label = version.join('.');
    const assets = new Map(release.assets.map(asset => [asset.name, asset]));
    const field = name => card.querySelector(`[data-field="${name}"]`);
    let size = null;
    for (const [fieldName, assetName] of Object.entries(assetNames(label))) {
      const asset = assets.get(assetName);
      const link = field(fieldName);
      if (!asset || !link) continue;
      link.href = asset.browser_download_url;
      size ??= asset.size;
    }
    field('version').textContent = `v${label}`;
    const date = release.published_at ? dateFormat.format(new Date(release.published_at)) : '';
    field('meta').textContent = [date, size ? sizeLabel(size) : ''].filter(Boolean).join(' · ');
  }

  async function loadReleases() {
    try {
      const response = await fetch('https://api.github.com/repos/Liis17/Hako/releases?per_page=50', { headers: { Accept: 'application/vnd.github+json' } });
      if (!response.ok) return;
      const releases = await response.json();
      fillPlatform('macos', latestRelease(releases, 'macos'), v => ({ primary: `Hako-${v}-macos.dmg` }));
      fillPlatform('windows', latestRelease(releases, 'windows'), v => ({ x64: `Hako-${v}-windows-x64.msix`, arm64: `Hako-${v}-windows-arm64.msix`, cer: 'Hako.cer' }));
    } catch {
      // Links keep pointing to GitHub Releases.
    }
  }

  async function loadMinecraftVersion() {
    try {
      const response = await fetch('https://piston-meta.mojang.com/mc/game/version_manifest_v2.json');
      if (!response.ok) return;
      const latest = (await response.json())?.latest?.release;
      if (typeof latest !== 'string' || !/^[\w.-]{1,20}$/.test(latest)) return;
      document.querySelectorAll('[data-mc-latest]').forEach(node => { node.textContent = latest; });
    } catch {
      // Keep the version baked into the page.
    }
  }

  const platform = (navigator.userAgentData?.platform || navigator.platform || navigator.userAgent || '').toLowerCase();
  const detected = platform.includes('mac') ? 'macos' : platform.includes('win') ? 'windows' : null;
  if (detected) document.querySelector(`.download-card[data-platform="${detected}"]`)?.classList.add('recommended');

  loadReleases();
  loadMinecraftVersion();

  const year = document.querySelector('#year');
  if (year) year.textContent = String(new Date().getFullYear());
})();
