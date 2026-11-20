"""YouTube Audio screenshot server: jellyfin-web's CSS, the Base bundle the plugin references, the plugin's pages, and the
user pages rendered through Base's status template the way StatusPage.Render does.

Usage: python3 -I server.py 8857
JELLYFIN_WEB names the jellyfin-web folder when it is not the macOS app's.
"""
import html, http.server, io, json, os, re, sys, urllib.parse, wave

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.join(HERE, '..', '..', 'Jellyfin.Plugin.YouTubeAudio')
REPO = os.path.join(PROJECT, 'Web')
JF = os.environ.get('JELLYFIN_WEB', '/Applications/Jellyfin.app/Contents/Resources/jellyfin-web')


def base_version():
    """The JPKribs.Jellyfin.Base version the plugin's csproj references."""
    csproj = open(os.path.join(PROJECT, 'Jellyfin.Plugin.YouTubeAudio.csproj'), encoding='utf-8').read()
    return re.search(r'Include="JPKribs\.Jellyfin\.Base"\s+Version="([^"]+)"', csproj).group(1)


BASEPKG = os.path.join(os.environ.get('NUGET_PACKAGES', os.path.expanduser('~/.nuget/packages')), 'jpkribs.jellyfin.base', base_version())
BASE = os.path.join(BASEPKG, 'assets')
STATUS = os.path.join(BASEPKG, 'templates', 'status.html')
for need, hint in ((BASEPKG, 'restore the plugin (dotnet restore) so NuGet caches the Base package'),
                   (os.path.join(JF, 'index.html'), 'set JELLYFIN_WEB to a jellyfin-web folder')):
    if not os.path.exists(need):
        sys.exit('Missing %s: %s.' % (need, hint))

TYPES = {'.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml', '.wav': 'audio/wav',
         '.woff2': 'font/woff2', '.woff': 'font/woff', '.png': 'image/png', '.ico': 'image/x-icon'}

# Generic sample data for the user pages: public domain works, with their composers as the artists.
QUEUE_ITEMS = [
    {'Title': 'Clair de lune', 'Artist': 'Claude Debussy', 'Album': 'Suite bergamasque', 'StatusCode': 1, 'Status': 'Downloading', 'Source': 'Apple Music', 'CreatedAt': '1'},
    {'Title': 'Gymnopédie No. 1', 'Artist': 'Erik Satie', 'Album': 'Trois Gymnopédies', 'StatusCode': 0, 'Status': 'Waiting to download', 'Source': 'Apple Music', 'CreatedAt': '2'},
    {'Title': 'Prelude in C Major, BWV 846', 'Artist': 'Johann Sebastian Bach', 'Album': 'The Well-Tempered Clavier, Book I', 'StatusCode': 0, 'Status': 'Waiting to download', 'Source': 'Spotify', 'CreatedAt': '3'},
    {'Title': 'Für Elise', 'Artist': 'Ludwig van Beethoven', 'Album': 'Piano Pieces', 'StatusCode': 2, 'Status': 'Downloaded, not yet added', 'Source': 'YouTube', 'CreatedAt': '4'},
    {'Title': 'Nocturne in E-flat Major, Op. 9 No. 2', 'Artist': 'Frédéric Chopin', 'Album': 'Nocturnes', 'StatusCode': 2, 'Status': 'Downloaded, not yet added', 'Source': 'Spotify', 'CreatedAt': '5'},
    {'Title': 'Spring: I. Allegro', 'Artist': 'Antonio Vivaldi', 'Album': 'The Four Seasons', 'StatusCode': 0, 'Status': 'Waiting to download', 'Source': 'Apple Music', 'CreatedAt': '6'},
    {'Title': 'Eine kleine Nachtmusik: I. Allegro', 'Artist': 'Wolfgang Amadeus Mozart', 'Album': 'Serenade No. 13', 'StatusCode': 0, 'Status': 'Waiting to download', 'Source': 'YouTube', 'CreatedAt': '7'},
    {'Title': 'Ave Maria', 'Artist': 'Franz Schubert', 'Album': 'Songs', 'StatusCode': 4, 'Status': 'Could not be downloaded', 'Source': 'Apple Music', 'CreatedAt': '8'},
]


def stylesheets():
    """jellyfin-web's stylesheets in load order: index.html's two first, the dark theme, then every chunk. A chunk that
    sets the root font to anything but the dashboard's 93% belongs to another layout, such as TV, and is left out."""
    first = re.findall(r'href="([^"?]+\.css)', open(os.path.join(JF, 'index.html')).read())
    rest = []
    for name in sorted(os.listdir(JF)):
        if not name.endswith('.css') or name in first:
            continue
        sizes = re.findall(r'html\{[^}]*?font-size:([^;}!]+)', open(os.path.join(JF, name), encoding='utf-8', errors='ignore').read())
        if any(size.strip() != '93%' for size in sizes):
            continue
        rest.append(name)
    return first + ['themes/dark/theme.css'] + rest


def silence():
    """A download's audio: 3:32 of 8-bit mono silence, so the player shows a song's length."""
    out = io.BytesIO()
    with wave.open(out, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(1)
        w.setframerate(4000)
        w.writeframes(bytes([128]) * 4000 * 212)
    return out.getvalue()


def translations():
    """The US English strings the plugin's Translations endpoint serves: the declared Jellyfin strings and the local ones."""
    csproj = open(os.path.join(PROJECT, 'Jellyfin.Plugin.YouTubeAudio.csproj'), encoding='utf-8').read()
    web = json.load(open(os.path.join(BASEPKG, 'jellyfin', 'web', 'en-us.json'), encoding='utf-8'))
    strings = {k: web[k] for k in re.findall(r'<JellyfinString Include="(\w+)"', csproj) if k in web}
    strings.update(json.load(open(os.path.join(PROJECT, 'Localization', 'en-us.json'), encoding='utf-8')))
    return strings


def fill(text, values):
    return re.sub(r'\{\{([A-Z_]+)\}\}', lambda m: values.get(m.group(1), m.group(0)), text)


def status_page(title, fragment):
    """Mirrors the controller's StatusPage.Render("Loading…", "", title, buttonText: null, showSpinner: true, content)."""
    return fill(open(STATUS, encoding='utf-8').read(), {
        'TITLE': html.escape(title), 'HEADING': html.escape('Loading…'), 'MESSAGE': '',
        'SPINNER': '<div class="base-spinner" role="status" aria-label="Loading"></div>', 'BUTTON': '', 'CONTENT': fragment})


def cover_svg(seed):
    palettes = [('#3a6ea5', '#7b4fd6'), ('#2a9d8f', '#1d3557'), ('#e76f51', '#6d597a'), ('#457b9d', '#a8dadc'), ('#8d5a97', '#e9c46a')]
    a, b = palettes[sum(map(ord, seed)) % len(palettes)]
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="600" height="600"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">'
            '<stop offset="0" stop-color="%s"/><stop offset="1" stop-color="%s"/></linearGradient></defs>'
            '<rect width="600" height="600" fill="url(#g)"/><circle cx="300" cy="300" r="150" fill="rgba(255,255,255,0.18)"/>'
            '<circle cx="300" cy="300" r="40" fill="rgba(0,0,0,0.25)"/></svg>') % (a, b)


class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, data, ct, code=200):
        if isinstance(data, str):
            data = data.encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', ct)
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def file(self, path):
        if not os.path.isfile(path):
            sys.stderr.write('404 %s\n' % self.path); sys.stderr.flush()
            return self.send(b'', 'text/plain', 404)
        return self.send(open(path, 'rb').read(), TYPES.get(os.path.splitext(path)[1], 'application/octet-stream'))

    def do_POST(self):
        return self.send(json.dumps({'Queued': 1}), 'application/json')

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        lp = u.path.lower()
        if u.path == '/stylesheets.json':
            return self.send(json.dumps(stylesheets()), 'application/json')
        if u.path.endswith('/configurationpage'):
            n = q['name'][0]
            if n.startswith('youtubeaudio_jpkribs_shared'):
                return self.file(os.path.join(BASE, 'jpkribs_shared' + os.path.splitext(n)[1]))
            if os.path.splitext(n)[1] in ('.js', '.css'):
                return self.file(os.path.join(REPO, n))
            return self.file(os.path.join(REPO, n + '.html'))
        if lp == '/youtube/download':
            return self.send(status_page('Download', open(os.path.join(REPO, 'youtubeaudio_download_public.html'), encoding='utf-8').read()), 'text/html; charset=utf-8')
        if lp == '/youtube/queue':
            return self.send(status_page('Queue', open(os.path.join(REPO, 'youtubeaudio_queue_public.html'), encoding='utf-8').read()), 'text/html; charset=utf-8')
        if lp == '/youtube/queue/items':
            return self.send(json.dumps(QUEUE_ITEMS), 'application/json')
        if lp == '/youtube/download/info':
            return self.send('{"Approved":true}', 'application/json')
        if lp == '/youtube/download/artists':
            return self.send('["Johann Sebastian Bach"]', 'application/json')
        if lp == '/youtube/download/albums':
            return self.send('["Cello Suites"]', 'application/json')
        if lp in ('/api/youtubeaudio/translations', '/youtubeaudio/translations'):
            return self.send(json.dumps(translations()), 'application/json')
        if lp == '/system/info/public':
            return self.send('{"ServerName":"Jellyfin","Id":"s1"}', 'application/json')
        if u.path.startswith('/api/YouTubeAudio/Downloads/'):
            if u.path.endswith('/Audio'):
                return self.send(silence(), 'audio/wav')
            return self.send(cover_svg(u.path), 'image/svg+xml')
        if u.path.startswith('/jf/'):
            return self.file(os.path.join(JF, u.path[4:]))
        if u.path.startswith('/web/'):
            return self.file(os.path.join(JF, u.path[5:]))
        return self.file(os.path.join(HERE, u.path.lstrip('/') or 'harness.html'))


http.server.ThreadingHTTPServer(('127.0.0.1', int(sys.argv[1])), H).serve_forever()
