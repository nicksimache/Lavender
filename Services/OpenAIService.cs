using OpenAI.Chat;
using OpenAI.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Services
{
    public class OpenAIService
    {
        private readonly ChatClient _client;
        
        /// <summary>
        /// Default constructor
        /// </summary>
        /// <exception cref="Exception"></exception>
        public OpenAIService()
        {
            string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new Exception("Missing OPENAI_API_KEY");

            _client = new(model: "gpt-4.1-mini", apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        }

        /// <summary>
        /// Returns AI response based on user input
        /// </summary>
        /// <param name="userPrompt"></param>
        /// <returns></returns>
        public async Task<string> AskAsync(string userPrompt)
        {
            ChatCompletion completion = await _client.CompleteChatAsync(userPrompt);

            return completion.Content[0].Text ?? "Error: Unable to generate response, please try again later";
        }
    }
}
