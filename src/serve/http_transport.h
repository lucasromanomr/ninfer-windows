#pragma once

#include "serve/generation_service.h"

#include <httplib.h>
#include <nlohmann/json.hpp>

#include <atomic>
#include <exception>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace ninfer::serve {

class ClientDisconnected final : public std::exception {
public:
    [[nodiscard]] const char* what() const noexcept override { return "client disconnected"; }
};

struct HttpGenerationStream {
    explicit HttpGenerationStream(PreparedRequest request) : prepared(std::move(request)) {}

    PreparedRequest prepared;
    std::atomic<bool> cancelled{false};
    bool started = false;
};

nlohmann::json parse_json_body(const httplib::Request& request);
[[nodiscard]] bool client_disconnected(const httplib::Request& request);

void prepare_sse_response(httplib::Response& response);
void write_stream_item(httplib::DataSink& sink, std::atomic<bool>& cancelled,
                       std::string_view item);
void write_stream_items(httplib::DataSink& sink, std::atomic<bool>& cancelled,
                        const std::vector<std::string>& items);
void set_owned_json_content(httplib::Response& response, std::string body,
                            std::shared_ptr<RequestLifetime> lifetime);

} // namespace ninfer::serve
