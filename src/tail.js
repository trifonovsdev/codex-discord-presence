'use strict';

const fs = require('fs');
const { StringDecoder } = require('string_decoder');

const READ_CHUNK_BYTES = 1024 * 1024;
const READ_BUDGET_BYTES = 8 * 1024 * 1024;
const MAX_PARTIAL_LINE_BYTES = 1024 * 1024;

/**
 * Incremental reader state for one append-only log. `skipPartial` is set when
 * reading starts in the middle of a file: the first line is then a fragment
 * and must not be parsed.
 */
function newTailState({ offset = 0, skipPartial = false } = {}) {
  return { offset, remainder: '', skipPartial, decoder: new StringDecoder('utf8'), lastSeen: Date.now() };
}

/**
 * Reads everything appended since the previous call and returns whole lines,
 * or null when nothing changed.
 *
 * Reads are chunked and budgeted: the first pass over a multi-megabyte log is
 * spread over several polls instead of being allocated as one buffer, and a
 * truncated log is re-read from the start.
 */
function readNewLines(filePath, state, size, { budgetBytes = READ_BUDGET_BYTES } = {}) {
  state.lastSeen = Date.now();
  if (size < state.offset) {
    state.offset = 0;
    state.remainder = '';
    state.skipPartial = false;
    state.decoder = new StringDecoder('utf8');
  }
  if (size === state.offset) return null;

  const lines = [];
  const buffer = Buffer.allocUnsafe(READ_CHUNK_BYTES);
  const descriptor = fs.openSync(filePath, 'r');
  try {
    let budget = budgetBytes;
    while (state.offset < size && budget > 0) {
      const length = Math.min(READ_CHUNK_BYTES, size - state.offset, budget);
      const read = fs.readSync(descriptor, buffer, 0, length, state.offset);
      if (read <= 0) break;
      state.offset += read;
      budget -= read;

      const parts = `${state.remainder}${state.decoder.write(buffer.subarray(0, read))}`.split(/\r?\n/);
      state.remainder = parts.pop() ?? '';
      if (state.remainder.length > MAX_PARTIAL_LINE_BYTES) state.remainder = '';
      if (state.skipPartial && parts.length) {
        parts.shift();
        state.skipPartial = false;
      }
      for (const part of parts) if (part) lines.push(part);
    }
  } finally {
    fs.closeSync(descriptor);
  }
  return lines;
}

module.exports = { newTailState, readNewLines, READ_BUDGET_BYTES };
