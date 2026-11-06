#include "core/gamedata_signature.h"

#include <cstdlib>
#include <vector>

extern "C"
{
#include "tweetnacl/tweetnacl.h"

    // TweetNaCl leaves randombytes to the embedder; only its key-generation functions call it, and
    // nothing here generates keys. Abort rather than hand out predictable "random" bytes.
    void randombytes(unsigned char*, unsigned long long) { std::abort(); }
}

namespace counterstrikesharp::update::signing {

std::optional<std::string> Base64Decode(const std::string& text)
{
    auto begin = text.find_first_not_of(" \t\r\n");
    auto end = text.find_last_not_of(" \t\r\n");
    if (begin == std::string::npos) return std::string();
    std::string_view input(text.data() + begin, end - begin + 1);

    std::string out;
    unsigned buffer = 0;
    int bits = 0;
    size_t padding = 0;
    for (char c : input)
    {
        int value;
        if (c >= 'A' && c <= 'Z') value = c - 'A';
        else if (c >= 'a' && c <= 'z')
            value = c - 'a' + 26;
        else if (c >= '0' && c <= '9')
            value = c - '0' + 52;
        else if (c == '+')
            value = 62;
        else if (c == '/')
            value = 63;
        else if (c == '=')
        {
            ++padding;
            continue;
        }
        else
            return std::nullopt;
        if (padding > 0) return std::nullopt; // data after '='
        buffer = (buffer << 6) | static_cast<unsigned>(value);
        bits += 6;
        if (bits >= 8)
        {
            bits -= 8;
            out.push_back(static_cast<char>((buffer >> bits) & 0xFF));
        }
    }
    if (padding > 2 || bits >= 6) return std::nullopt;
    return out;
}

bool VerifySignature(const std::string& message, const std::string& signatureText, const std::string& publicKeyBase64, std::string& error)
{
    auto key = Base64Decode(publicKeyBase64);
    if (!key || key->size() != crypto_sign_PUBLICKEYBYTES)
    {
        error = "the signing public key is not 32 bytes of base64";
        return false;
    }
    auto signature = Base64Decode(signatureText);
    if (!signature || signature->size() != crypto_sign_BYTES)
    {
        error = "the signature is not 64 bytes of base64";
        return false;
    }

    // TweetNaCl verifies the NaCl "signed message" form, signature || message.
    std::vector<unsigned char> signedMessage(signature->begin(), signature->end());
    signedMessage.insert(signedMessage.end(), message.begin(), message.end());
    std::vector<unsigned char> opened(signedMessage.size());
    unsigned long long openedLength = 0;
    if (crypto_sign_open(opened.data(), &openedLength, signedMessage.data(), signedMessage.size(),
                         reinterpret_cast<const unsigned char*>(key->data())) != 0)
    {
        error = "the signature does not verify";
        return false;
    }
    return true;
}

bool VerifySignatureWithAnyKey(const std::string& message,
                               const std::string& signatureText,
                               const std::string& publicKeys,
                               std::string& error)
{
    size_t tried = 0;
    size_t pos = 0;
    while (pos < publicKeys.size())
    {
        const auto start = publicKeys.find_first_not_of(" \t\r\n,", pos);
        if (start == std::string::npos) break;
        auto end = publicKeys.find_first_of(" \t\r\n,", start);
        if (end == std::string::npos) end = publicKeys.size();
        ++tried;
        if (VerifySignature(message, signatureText, publicKeys.substr(start, end - start), error)) return true;
        pos = end;
    }
    if (tried == 0) error = "no signing public key configured";
    else if (tried > 1)
        error += " (with any of " + std::to_string(tried) + " keys)";
    return false;
}

} // namespace counterstrikesharp::update::signing
