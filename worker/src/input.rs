use anyhow::{ensure, Context, Result};
use same_file::Handle;
use std::{
    fs::File,
    io::{BufRead, BufReader, Read, Seek, SeekFrom},
    path::{Path, PathBuf},
};

#[derive(Clone, Copy)]
enum Encoding {
    Utf8,
    Utf16(bool),
    Utf32(bool),
}

impl Encoding {
    fn detect(prefix: &[u8]) -> (Self, usize) {
        match prefix {
            [0xff, 0xfe, 0, 0, ..] => (Self::Utf32(true), 4),
            [0, 0, 0xfe, 0xff, ..] => (Self::Utf32(false), 4),
            [0xff, 0xfe, ..] => (Self::Utf16(true), 2),
            [0xfe, 0xff, ..] => (Self::Utf16(false), 2),
            [0xef, 0xbb, 0xbf, ..] => (Self::Utf8, 3),
            _ => (Self::Utf8, 0),
        }
    }

    fn width(self) -> usize {
        match self {
            Self::Utf8 => 1,
            Self::Utf16(_) => 2,
            Self::Utf32(_) => 4,
        }
    }

    fn unit(self, bytes: &[u8]) -> u32 {
        match self {
            Self::Utf8 => bytes[0] as u32,
            Self::Utf16(le) => {
                let pair = [bytes[0], bytes[1]];
                (if le {
                    u16::from_le_bytes(pair)
                } else {
                    u16::from_be_bytes(pair)
                }) as u32
            }
            Self::Utf32(le) => {
                let word = [bytes[0], bytes[1], bytes[2], bytes[3]];
                if le {
                    u32::from_le_bytes(word)
                } else {
                    u32::from_be_bytes(word)
                }
            }
        }
    }

    fn decode(self, bytes: &[u8]) -> String {
        if matches!(self, Self::Utf8) {
            return String::from_utf8_lossy(bytes).into_owned();
        }
        let units = bytes.chunks_exact(self.width()).map(|c| self.unit(c));
        let mut result: String = match self {
            Self::Utf16(_) => char::decode_utf16(units.map(|c| c as u16))
                .map(|r| r.unwrap_or(char::REPLACEMENT_CHARACTER))
                .collect(),
            _ => units
                .map(|c| char::from_u32(c).unwrap_or(char::REPLACEMENT_CHARACTER))
                .collect(),
        };
        if !bytes.len().is_multiple_of(self.width()) {
            result.push(char::REPLACEMENT_CHARACTER);
        }
        result
    }
}

const BATCH: usize = 1024 * 1024;
const MAX_LINE: usize = 8 * BATCH;
pub struct Input {
    path: PathBuf,
    id: Handle,
    encoding: Encoding,
    bom: usize,
    file: BufReader<File>,
    pub position: u64,
    pub limit: u64,
}

impl Input {
    pub fn open(path: &Path) -> Result<Self> {
        let file = File::open(path).context("opening ACT log")?;
        let mut prefix = [0u8; 4];
        let n = (&file).read(&mut prefix)?;
        let (encoding, bom) = Encoding::detect(&prefix[..n]);
        Ok(Self {
            encoding,
            bom,
            path: path.into(),
            id: Handle::from_file(file.try_clone()?)?,
            limit: file.metadata()?.len(),
            file: BufReader::new(file),
            position: 0,
        })
    }

    pub fn changed(&self) -> Result<bool> {
        Ok(self.id != Handle::from_path(&self.path)?
            || self.file.get_ref().metadata()?.len() < self.limit)
    }

    pub fn refresh(&mut self) -> Result<bool> {
        let size = self.file.get_ref().metadata()?.len();
        if size < self.limit {
            self.position = 0;
        }
        self.limit = size;
        match Handle::from_path(&self.path) {
            Ok(id) => Ok(self.id != id),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
            Err(error) => Err(error.into()),
        }
    }

    pub fn rewind(&mut self) -> Result<()> {
        ensure!(!self.changed()?, "Log replaced or truncated during replay");
        self.position = 0;
        Ok(())
    }

    pub fn tail(&mut self) -> Result<String> {
        let start = self.limit.saturating_sub(8192);
        self.file.seek(SeekFrom::Start(start))?;
        let mut bytes = Vec::new();
        self.file
            .by_ref()
            .take(self.limit - start)
            .read_to_end(&mut bytes)?;
        let skip = ((self.encoding.width() as u64 - start % self.encoding.width() as u64)
            % self.encoding.width() as u64) as usize;
        Ok(self.encoding.decode(bytes.get(skip..).unwrap_or_default()))
    }

    // Return consumed source bytes, or None for an unfinished live line.
    fn read_record(&mut self, raw: &mut Vec<u8>, final_line: bool) -> Result<Option<u64>> {
        let mut consumed = 0u64;
        if self.position == 0 {
            // An initially empty live file may acquire its BOM on the first append.
            let prefix = self.file.fill_buf()?;
            (self.encoding, self.bom) =
                Encoding::detect(&prefix[..prefix.len().min(self.limit.min(4) as usize)]);
            self.file.consume(self.bom);
            consumed = self.bom as u64;
        }
        let width = self.encoding.width();
        while self.position + consumed < self.limit {
            let remaining = self.limit - self.position - consumed;
            let mut word = [0; 4];
            let (length, delimiter) = if width == 1 {
                let available = self.file.fill_buf()?;
                ensure!(
                    !available.is_empty(),
                    "Log truncated at byte {}",
                    self.position + consumed
                );
                let available = &available[..available
                    .len()
                    .min(remaining.min(usize::MAX as u64) as usize)];
                let delimiter = memchr::memchr2(b'\r', b'\n', available);
                let size = delimiter.unwrap_or(available.len());
                ensure!(
                    raw.len() + size <= MAX_LINE,
                    "Line exceeds 8 MiB at byte {}",
                    self.position
                );
                raw.extend_from_slice(&available[..size]);
                let ending = delimiter.map(|n| available[n] as u32);
                let length = size + usize::from(ending.is_some());
                self.file.consume(length);
                (length, ending)
            } else {
                let n = width.min(remaining as usize);
                self.file.read_exact(&mut word[..n])?;
                let unit = self.encoding.unit(&word);
                if n == width && matches!(unit, 10 | 13) {
                    (n, Some(unit))
                } else {
                    ensure!(
                        raw.len() + n <= MAX_LINE,
                        "Line exceeds 8 MiB at byte {}",
                        self.position
                    );
                    raw.extend_from_slice(&word[..n]);
                    (n, None)
                }
            };
            consumed += length as u64;
            if let Some(end) = delimiter {
                if end == 13 {
                    let remaining = self.limit - self.position - consumed;
                    if remaining < width as u64 && !final_line {
                        return Ok(None);
                    }
                    if remaining >= width as u64 {
                        self.file.read_exact(&mut word[..width])?;
                        if self.encoding.unit(&word) == 10 {
                            consumed += width as u64;
                        } else {
                            self.file.seek_relative(-(width as i64))?;
                        }
                    }
                }
                return Ok(Some(consumed));
            }
        }
        Ok(final_line.then_some(consumed))
    }

    pub fn batch(&mut self, live: bool, final_line: bool) -> Result<(Vec<String>, bool)> {
        self.file.seek(SeekFrom::Start(self.position))?;
        let mut lines = Vec::new();
        let mut size = 2;
        let mut raw = Vec::new();
        while self.position < self.limit && lines.len() < 4096 {
            raw.clear();
            let Some(consumed) = self.read_record(&mut raw, final_line)? else {
                return Ok((lines, true));
            };
            if self.position == 0 && consumed == self.bom as u64 {
                self.position += consumed;
                break;
            }
            let mut line = self.encoding.decode(&raw);
            if live && line.trim().len() != line.len() {
                line = line.trim().to_owned();
            }
            if live && line.contains('\0') {
                line.retain(|c| c != '\0');
                line = line.trim().to_owned();
            }
            // Count the UTF-8 bytes including JSON quotes, escapes and separator.
            let encoded = 3 + line
                .bytes()
                .map(|b| match b {
                    b'"' | b'\\' | b'\n' | b'\r' | b'\t' | 8 | 12 => 2,
                    0..=31 => 6,
                    _ => 1,
                })
                .sum::<usize>();
            if !lines.is_empty() && size + encoded > BATCH {
                break;
            }
            self.position += consumed;
            if !live || !line.is_empty() {
                lines.push(line);
                size += encoded;
            }
        }
        Ok((lines, self.position >= self.limit))
    }
}
