// The picture picker's pure helpers: TMDB listing addresses, asset addresses, pre-upload checks.
const P = require('../../Jellyfin.Plugin.DiscMenus/Web/editor/pictures.js');
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };
const MB = 1024 * 1024;

check(P.tmdbPath('https://image.tmdb.org/t/p/original/abc123XY_-9.jpg') === '/abc123XY_-9.jpg' && P.tmdbPath('https://image.tmdb.org/t/p/w1280/x.png') === '/x.png', 'a TMDB address gives the path the menu stores');
for (const bad of ['http://image.tmdb.org/t/p/original/a.jpg', 'https://evil.example/t/p/original/a.jpg', 'https://image.tmdb.org/t/p/original/a.jpg?x=1', 'https://image.tmdb.org/t/p/original/a.svg', 'https://image.tmdb.org/t/p/original/../a.jpg', 'https://image.tmdb.org.evil.example/t/p/original/a.jpg', 'https://image.tmdb.org/t/p/original/a b.jpg', '', null, undefined, 5, {}]) {
  check(P.tmdbPath(bad) === null && P.tmdbThumb(bad) === null, 'refuses ' + JSON.stringify(bad));
}
check(P.tmdbThumb('https://image.tmdb.org/t/p/w300/a.jpg') === 'https://image.tmdb.org/t/p/w300/a.jpg', 'a thumbnail address is returned as it is');
check(P.assetRoute('asset:3f2b8c1e-6d4a/pic.png') === 'DiscMenus/Assets/3f2b8c1e-6d4a/pic.png', 'an asset reference becomes the route that serves it');
for (const bad of ['asset:a/../b.png', 'asset:a/b/c.png', 'asset:/b.png', 'asset:a/', 'https://x/y.png', 'asset:-a/b.png', 'asset:a/.png', '', null]) check(P.assetRoute(bad) === null, 'no route for ' + JSON.stringify(bad));
check(P.kindOfName('A.PNG') === 'image' && P.kindOfName('s.Mp3') === 'audio' && P.kindOfName('x.svg') === null && P.kindOfName('noext') === null && P.kindOfName('a.png.exe') === null, 'files are told apart by extension, case-insensitively');
check(P.precheck({ name: 'a.png', size: 1000 }, 'image') === null && P.precheck({ name: 'a.wav', size: 1000 }, 'audio') === null && P.precheck({ name: 'a.mp3', size: 1000 }) === null, 'ordinary files pass');
check(/needs a picture/.test(P.precheck({ name: 'a.mp3', size: 10 }, 'image')) && /needs a sound/.test(P.precheck({ name: 'a.png', size: 10 }, 'audio')), 'the wrong kind of file says what is needed');
check(/can't be used/.test(P.precheck({ name: 'a.svg', size: 10 }, 'image')) && /can't be used/.test(P.precheck({ name: 'a.gif', size: 10 })), 'svg and other types are refused');
check(P.precheck({ name: 'a.png', size: 5 * MB + 1 }, 'image') !== null && P.precheck({ name: 'a.png', size: 5 * MB }, 'image') === null, 'the picture limit is 5 MB');
check(P.precheck({ name: 'a.wav', size: 25 * MB + 1 }, 'audio') !== null && P.precheck({ name: 'a.wav', size: 25 * MB }, 'audio') === null, 'the sound limit is 25 MB');
check(P.precheck({ name: 'a.png', size: 0 }, 'image') === 'That file is empty.' && P.precheck(null) === 'Choose a file.' && P.precheck({}) === 'Choose a file.', 'empty and missing files are refused');
check(P.size(100) === '1 KB' && P.size(2048) === '2 KB' && P.size(1.5 * MB) === '1.5 MB' && P.size(-1) === '' && P.size(NaN) === '', 'sizes are readable');
check(P.accept('image') === '.png,.jpg,.jpeg,.webp' && P.accept('audio') === '.mp3,.ogg,.opus,.m4a,.wav', 'file inputs only offer the right kind');
console.log('failures: ' + fail); process.exit(fail ? 1 : 0);
