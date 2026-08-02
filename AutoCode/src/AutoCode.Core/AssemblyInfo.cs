// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Runtime.CompilerServices;

// Parsers for skill and agent manifests are internal because they are not part of the public
// surface, but they carry enough logic to deserve direct tests rather than only being exercised
// through the filesystem.
[assembly: InternalsVisibleTo("AutoCode.Tests")]
