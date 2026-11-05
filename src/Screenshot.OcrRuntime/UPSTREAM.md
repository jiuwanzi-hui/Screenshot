# Windows OCR runtime

Source: RapidOcrNet 3.0.0, commit `590d44b2efc7ecbef446c36bb6359ebbfcffeb50`.
https://github.com/BobLd/RapidOcrNet/tree/590d44b2efc7ecbef446c36bb6359ebbfcffeb50/RapidOcrNet

Apache-2.0; original headers and LICENSE.txt retained. This project is used only
by Screenshot.App (Windows). macOS continues using its existing dependency.
Models, preprocessing, CTC decoding rules and coordinate mapping are unchanged.

Local changes: contiguous SIMD score scanning in TextRecognizer/CtcArgMax,
avoiding multidimensional tensor indexing for every vocabulary entry; bounded
independent row recognition sharing the existing ONNX session/thread pool,
retaining each crop's original dimensions and output order (no padded batches).
Project packaging omits upstream demo models and Linux native assets.
