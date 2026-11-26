/** Extract one historical source without publishing adjacent archive records. */
export function extractArchiveSection(markdown: string, section: string): string {
  const lines = markdown.split(/\r?\n/);
  const start = `<!-- archive:start ${section} -->`;
  const end = `<!-- archive:end ${section} -->`;
  const starts = lines.flatMap((line, index) => line === start ? [index] : []);
  const ends = lines.flatMap((line, index) => line === end ? [index] : []);
  if (starts.length !== 1 || ends.length !== 1 || ends[0] <= starts[0]) {
    throw new Error(`Archive section must have one ordered marker pair: ${section}`);
  }
  const content = lines.slice(starts[0] + 1, ends[0]);
  if (content.some((line) => /^<!-- archive:(start|end) /.test(line))) {
    throw new Error(`Archive section contains another section boundary: ${section}`);
  }
  let fence: string | undefined;
  return content.map((line) => {
    const delimiter = line.match(/^\s*(`{3,}|~{3,})/)?.[1];
    if (delimiter) {
      if (!fence) fence = delimiter;
      else if (delimiter[0] === fence[0] && delimiter.length >= fence.length) fence = undefined;
      return line;
    }
    if (fence) return line;
    // Archive headings are two levels deeper than the original document.
    return line.replace(/^#{3,6}(?= )/, (heading) => heading.slice(2));
  }).join("\n");
}
