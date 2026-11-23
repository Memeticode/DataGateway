using System.Runtime.CompilerServices;

// The library's internals stay internal; the host-level tests reach them to pin the wire, the API document and the
// failure log without widening the public surface. (An assembly attribute takes no XML doc comment.)
[assembly: InternalsVisibleTo("DataGateway.Web.Tests")]
