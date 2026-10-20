import { test } from "node:test";
import assert from "node:assert/strict";
import { jpegSize } from "../lib/jpeg.js";

// SOI, an APP0 segment, then SOF0 for a 1280×720 frame.
function fakeJpeg(width, height) {
  const bytes = [
    0xff, 0xd8,
    0xff, 0xe0, 0x00, 0x04, 0x00, 0x00,
    0xff, 0xc0, 0x00, 0x11, 0x08, height >> 8, height & 255, width >> 8, width & 255, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0,
  ];
  return Buffer.from(bytes).toString("base64");
}

test("reads the frame size past other segments", () => {
  assert.deepEqual(jpegSize(fakeJpeg(1280, 720)), { width: 1280, height: 720 });
  assert.deepEqual(jpegSize(fakeJpeg(853, 1280)), { width: 853, height: 1280 });
});

test("not a JPEG, truncated or not base64 → null", () => {
  assert.equal(jpegSize(Buffer.from("PNG....").toString("base64")), null);
  assert.equal(jpegSize(fakeJpeg(10, 10).slice(0, 8)), null);
  assert.equal(jpegSize("%%%"), null);
});
