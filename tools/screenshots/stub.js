// A stand-in ApiClient with generic sample data for the documentation screenshots.
// Every song is a public domain work, credited to its composer.
window.__posts = [];
window.__errors = [];
window.addEventListener('error', function (e) { window.__errors.push(String(e.message)); });
window.addEventListener('unhandledrejection', function (e) { window.__errors.push('rejection: ' + (e.reason && e.reason.message || e.reason)); });

window.__config = {
    AudioFormat: 'Opus', MusicLibraryId: 'L1', MusicLibraryPath: '/media/music', ReplaceDuplicates: true, LookUpCatalogMetadata: true,
    EmbedCoverArt: true, GeneratedCoverTextOrder: 'Album,Artist,Year', GeneratedCoverText: 'Album,Year', GeneratedCoverColors: 'Album',
    CacheDirectoryOverride: '', YtDlpPath: '', ApprovedUserIds: ['u3', 'u5'], MaxPublicPlaylistItems: 200
};

function yt(id) { return 'https://www.youtube.com/watch?v=' + id; }
window.__queue = [
    { Id: 'q1', Title: 'Clair de lune', Artist: 'Claude Debussy', Source: 'applemusic', Url: yt('aB3dE5fG7hJ'), StatusCode: 1, Status: 'Downloading' },
    { Id: 'q2', Title: 'Gymnopédie No. 1', Artist: 'Erik Satie', Source: 'applemusic', Url: yt('kL9mN1pQ3rS'), StatusCode: 0, Status: 'Queued',
      MatchNote: 'Length differs by 6s; Not uploaded by the artist. Check the match before importing.' },
    { Id: 'q3', Title: 'Prelude in C Major, BWV 846', Artist: 'Johann Sebastian Bach', Source: 'spotify', Url: '', StatusCode: 0, Status: 'Queued' },
    { Id: 'q4', Title: 'Spring: I. Allegro', Artist: 'Antonio Vivaldi', Source: 'applemusic', Url: '', StatusCode: 0, Status: 'Queued' },
    { Id: 'q5', Title: 'Eine kleine Nachtmusik: I. Allegro', Url: yt('tU5vW7xY9zA'), StatusCode: 0, Status: 'Queued' },
    { Id: 'q6', Title: 'Für Elise', Artist: 'Ludwig van Beethoven', Source: 'spotify', Url: yt('cD2eF4gH6iJ'), StatusCode: 2, Status: 'Downloaded',
      ReplacesFileCount: 1 },
    { Id: 'q7', Title: 'Nocturne in E-flat Major, Op. 9 No. 2', Artist: 'Frédéric Chopin', Source: 'spotify', Url: yt('mN8oP0qR2sT'), StatusCode: 2, Status: 'Downloaded' },
    { Id: 'q8', Title: 'Ave Maria', Artist: 'Franz Schubert', Source: 'applemusic', Url: '', StatusCode: 4, Status: 'Error',
      ErrorMessage: 'No matching YouTube upload found for "Franz Schubert Ave Maria".' }
];

window.__downloads = [
    { Id: 'd1', MetadataTitle: 'Für Elise', FileName: 'Fur Elise.opus', Artist: 'Ludwig van Beethoven', AlbumArtist: 'Ludwig van Beethoven', FeaturedArtist: '',
      Album: 'Piano Pieces', Year: 1810, Genre: 'Classical', TrackNumber: 2, FileSize: 3920000, HasCover: true, InLibrary: false },
    { Id: 'd2', MetadataTitle: 'Nocturne in E-flat Major, Op. 9 No. 2', FileName: 'Nocturne Op 9 No 2.opus', Artist: 'Frédéric Chopin', AlbumArtist: 'Frédéric Chopin',
      FeaturedArtist: '', Album: 'Nocturnes', Year: 1832, Genre: 'Classical; Romantic', TrackNumber: 2, FileSize: 4710000, HasCover: true, InLibrary: true },
    { Id: 'd3', MetadataTitle: 'Gymnopédie No. 1', FileName: 'Gymnopedie No 1.opus', Artist: 'Erik Satie', AlbumArtist: '', FeaturedArtist: '',
      Album: '', Year: null, Genre: '', TrackNumber: null, FileSize: 3350000, HasCover: false, InLibrary: false }
];

var USERS = [
    { Id: 'u1', Name: 'admin', Policy: { IsAdministrator: true } },
    { Id: 'u2', Name: 'alex', Policy: { IsAdministrator: false } },
    { Id: 'u3', Name: 'jordan', Policy: { IsAdministrator: false } },
    { Id: 'u4', Name: 'sam', Policy: { IsAdministrator: false } },
    { Id: 'u5', Name: 'taylor', Policy: { IsAdministrator: false } }
];

function clone(o) { return JSON.parse(JSON.stringify(o)); }
function ok(o) { return Promise.resolve(clone(o)); }

window.ApiClient = {
    serverAddress: function () { return 'http://jellyfin.example.com:8096'; },
    serverId: function () { return 's1'; },
    getCurrentUserId: function () { return 'u1'; },
    accessToken: function () { return 't'; },
    getUrl: function (p, q) { return '/api/' + p + (q ? '?' + new URLSearchParams(q) : ''); },
    getImageUrl: function () { return ''; },
    getPluginConfiguration: function () { return ok(window.__config); },
    updatePluginConfiguration: function (id, cfg) { window.__posts.push({ url: 'config', body: clone(cfg) }); window.__config = clone(cfg); return ok({}); },
    getUsers: function () { return ok(USERS); },
    getUserImageUrl: function () { return ''; },
    getJSON: function () { return ok([]); },
    getScheduledTasks: function () { return ok([]); },
    sendMessage: function () {}, sendWebSocketMessage: function () {},
    ajax: function (o) { return window.ApiClient.fetch(o); },
    fetch: function (o) {
        window.__posts.push({ url: o.url, type: o.type });
        var u = o.url.replace(/^.*\/YouTubeAudio\//, '');
        if (u === 'Queue' && o.type === 'GET') return ok({ Items: window.__queue });
        if (u === 'Downloads') return ok({ Items: window.__downloads });
        if (u === 'Libraries') return ok([{ Id: 'L1', Name: 'Music', Path: '/media/music' }, { Id: 'L2', Name: 'Classical', Path: '/media/classical' }]);
        if (u === 'Playlists/Sources') return ok([{ Id: 'applemusic', Name: 'Apple Music' }, { Id: 'applemusiclibrary', Name: 'Apple Music Library' }, { Id: 'spotify', Name: 'Spotify' }]);
        if (u === 'Playlists') return ok([
            { Id: 'p1', Name: 'Evening Piano', UserName: 'taylor', Added: 2, Total: 6 },
            { Id: 'p2', Name: 'Baroque Strings', UserName: 'taylor', Added: 0, Total: 5 }]);
        if (u.indexOf('Songs?') === 0) return ok([{ Id: 's1', Title: 'Clair de lune', Artist: 'Claude Debussy', Album: 'Suite bergamasque' }]);
        if (u.indexOf('Artists') === 0) return ok(['Claude Debussy', 'Erik Satie']);
        if (u.indexOf('Albums') === 0) return ok(['Suite bergamasque']);
        return ok({});
    }
};
window.Dashboard = { alert: function (m) { window.__errors.push('alert: ' + m); } };
window.LibraryMenu = { setTabs: function () {} };
